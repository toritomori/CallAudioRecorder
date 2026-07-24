using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CallAudioRecorder.Models;
using CallAudioRecorder.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace CallAudioRecorder;

public partial class MainWindow : Window
{
    private const string DefaultOllamaModel = "qwen3.5:9b";

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<TranscriptEntry> _transcript = new();
    private readonly OllamaClient _ollama = new();

    private RecordingEngine? _engine;
    private TranscriptionService? _stt;
    private IReadOnlyList<TranscriptEntry>? _finalTranscript;
    private CancellationTokenSource? _summaryCts;
    private string? _lastFile;
    private double _sysVu, _micVu;
    private bool _busy; // защита от повторного клика во время старта/финализации

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CallAudioRecorder", "settings.json");

    public MainWindow()
    {
        InitializeComponent();

        OutputFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        TranscriptList.ItemsSource = _transcript;
        LoadSettings();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += UiTimer_Tick;

        LoadDevices();
        _ = LoadOllamaModelsAsync();

        Closed += (_, _) =>
        {
            _stt?.Dispose();
            _engine?.Dispose();
            _enumerator.Dispose();
            _ollama.Dispose();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _summaryCts?.Cancel();
        if (_engine is not null) StopRecordingCore(); // дописать mp3; транскрипт добьём без ожидания
        base.OnClosing(e);
    }

    private void LoadDevices()
    {
        RenderDevices.Items.Clear();
        CaptureDevices.Items.Clear();

        var defaultRender = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var item = new DeviceItem(dev);
            RenderDevices.Items.Add(item);
            if (dev.ID == defaultRender.ID) RenderDevices.SelectedItem = item;
        }

        var defaultCapture = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var item = new DeviceItem(dev);
            CaptureDevices.Items.Add(item);
            if (dev.ID == defaultCapture.ID) CaptureDevices.SelectedItem = item;
        }
    }

    private async Task LoadOllamaModelsAsync()
    {
        try
        {
            var models = await _ollama.ListModelsAsync();
            OllamaModels.Items.Clear();
            foreach (var m in models) OllamaModels.Items.Add(m);
            OllamaModels.SelectedItem = models.FirstOrDefault(m => m.StartsWith(DefaultOllamaModel))
                                        ?? models.FirstOrDefault();
        }
        catch (OllamaUnavailableException)
        {
            OllamaModels.Items.Clear();
            OllamaModels.Items.Add(DefaultOllamaModel);
            OllamaModels.SelectedIndex = 0;
        }
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_engine is null) await StartRecordingAsync();
        else await StopRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (RenderDevices.SelectedItem is not DeviceItem render ||
            CaptureDevices.SelectedItem is not DeviceItem capture)
        {
            Status.Text = "Выберите устройство вывода и микрофон.";
            return;
        }

        var folder = OutputFolder.Text.Trim();
        if (!Directory.Exists(folder))
        {
            Status.Text = $"Папка не найдена: {folder}";
            return;
        }

        bool transcribe = TranscribeCheck.IsChecked == true;
        _busy = true;
        try
        {
            if (transcribe && !ModelDownloader.ModelsPresent())
            {
                var progress = new Progress<string>(s => Status.Text = s);
                try
                {
                    await ModelDownloader.EnsureAsync(progress);
                }
                catch (Exception ex)
                {
                    Status.Text = $"Модели не загрузились: {ex.Message} Запись без транскрипта.";
                    transcribe = false;
                }
            }

            int bitrate = Br128.IsChecked == true ? 128 : Br320.IsChecked == true ? 320 : 192;
            var path = Path.Combine(folder, $"Запись_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");

            try
            {
                _engine = new RecordingEngine(render.Device, capture.Device, path, bitrate, transcribe)
                {
                    SystemGain = (float)SystemGain.Value,
                    MicGain = (float)MicGain.Value
                };

                if (transcribe)
                {
                    Status.Text = "Загружаю модель распознавания…";
                    var engine = _engine;
                    var hotwords = TranscriptCorrector.ToHotwords(GlossaryBox.Text);
                    bool diarize = DiarizeCheck.IsChecked == true;
                    _stt = await Task.Run(() => new TranscriptionService(hotwords, diarize));
                    _stt.EntryRecognized += entry => Dispatcher.BeginInvoke(() => AddTranscriptEntry(entry));
                    _stt.Start(engine.MicTap16k!, engine.SystemTap16k!, () => engine.Elapsed);
                }

                _engine.Start();
            }
            catch (Exception ex)
            {
                _stt?.Dispose();
                _stt = null;
                _engine?.Dispose();
                _engine = null;
                Status.Text = $"Не удалось начать запись: {ex.Message}";
                return;
            }

            _transcript.Clear();
            _finalTranscript = null;
            MakeSummaryBtn.IsEnabled = false;
            TranscriptEmpty.Visibility = Visibility.Visible;

            _uiTimer.Start();
            SetRecordingUi(true);
            Status.Text = $"Идёт запись: {Path.GetFileName(path)}" + (transcribe ? " · транскрипция включена" : "");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_engine is null) return;
        _busy = true;
        try
        {
            string path = StopRecordingCore();

            if (_stt is { } stt)
            {
                Status.Text = "Распознаю остаток речи…";
                _finalTranscript = await Task.Run(stt.StopAndDrain);
                stt.Dispose();
                _stt = null;

                if (_finalTranscript.Count > 0)
                {
                    SaveTranscript(path, _finalTranscript);
                    MakeSummaryBtn.IsEnabled = true;
                    FixTextBtn.IsEnabled = true;
                }
            }

            var size = new FileInfo(path).Length / 1024.0 / 1024.0;
            var extra = _finalTranscript is { Count: > 0 }
                ? $" · реплик: {_finalTranscript.Count}" : "";
            Status.Text = $"Сохранено: {Path.GetFileName(path)} ({size:F1} МБ){extra}";
            OpenFolderBtn.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Синхронная часть остановки: mp3 дописан, UI переключён.</summary>
    private string StopRecordingCore()
    {
        _uiTimer.Stop();
        string path = _engine!.OutputPath;
        try
        {
            _engine.Stop();
        }
        finally
        {
            _engine.Dispose();
            _engine = null;
        }

        SetRecordingUi(false);
        _sysVu = _micVu = 0;
        SystemVu.Value = MicVu.Value = 0;
        _lastFile = path;
        return path;
    }

    private void MergeGap_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TranscriptionService.BubbleMergeGap = TimeSpan.FromSeconds(e.NewValue);
        if (IsLoaded) SaveSettings(); // не писать на диск во время InitializeComponent
    }

    private void Glossary_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

    private void Diarize_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings(); // не писать на диск во время InitializeComponent
    }

    /// <summary>Прогоняет транскрипт через LLM: восстанавливает английские термины и чинит ошибки распознавания.</summary>
    private async void FixText_Click(object sender, RoutedEventArgs e)
    {
        if (_finalTranscript is not { Count: > 0 } transcript || _lastFile is null) return;
        if (OllamaModels.SelectedItem is not string model || string.IsNullOrWhiteSpace(model))
        {
            Status.Text = "Выберите модель Ollama во вкладке «Итоги».";
            return;
        }

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();

        FixTextBtn.IsEnabled = false;
        MakeSummaryBtn.IsEnabled = false;
        Status.Text = $"Исправляю текст ({model})…";

        try
        {
            var fixedEntries = await TranscriptCorrector.CorrectAsync(
                _ollama, model, transcript, GlossaryBox.Text, progress: null, ct: _summaryCts.Token);

            _finalTranscript = fixedEntries;
            _transcript.Clear();
            foreach (var entry in fixedEntries) _transcript.Add(entry);
            TranscriptScroll.ScrollToEnd();

            SaveTranscript(_lastFile, fixedEntries); // перезаписываем .транскрипт.md исправленным
            Status.Text = $"Текст исправлен · реплик: {fixedEntries.Count}";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Исправление отменено.";
        }
        catch (Exception ex) when (ex is OllamaUnavailableException or OllamaModelMissingException)
        {
            Status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Status.Text = $"Ошибка исправления: {ex.Message}";
        }
        finally
        {
            FixTextBtn.IsEnabled = true;
            MakeSummaryBtn.IsEnabled = true;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("mergeGapSeconds", out var gap))
                MergeGapSlider.Value = Math.Clamp(gap.GetDouble(), MergeGapSlider.Minimum, MergeGapSlider.Maximum);
            if (doc.RootElement.TryGetProperty("glossary", out var gl))
                GlossaryBox.Text = gl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("diarizeSpeakers", out var di))
                DiarizeCheck.IsChecked = di.GetBoolean();
        }
        catch
        {
            // повреждённый settings.json не должен мешать запуску
        }
        TranscriptionService.BubbleMergeGap = TimeSpan.FromSeconds(MergeGapSlider.Value);
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                mergeGapSeconds = MergeGapSlider.Value,
                glossary = GlossaryBox.Text,
                diarizeSpeakers = DiarizeCheck.IsChecked == true
            }));
        }
        catch
        {
            // недоступный диск не критичен для работы
        }
    }

    /// <summary>
    /// Добавляет реплику в живую ленту: если тот же спикер продолжает говорить
    /// (пауза &lt; BubbleMergeGap), текст доклеивается в последний пузырь.
    /// </summary>
    private void AddTranscriptEntry(TranscriptEntry entry)
    {
        var last = _transcript.Count > 0 ? _transcript[^1] : null;
        if (last is not null && last.Speaker == entry.Speaker &&
            entry.StartTime - last.End <= TranscriptionService.BubbleMergeGap)
        {
            _transcript[^1] = last.MergeWith(entry);
        }
        else
        {
            _transcript.Add(entry);
        }
        TranscriptEmpty.Visibility = Visibility.Collapsed;
        TranscriptScroll.ScrollToEnd();
    }

    private static void SaveTranscript(string mp3Path, IReadOnlyList<TranscriptEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Транскрипт: {Path.GetFileNameWithoutExtension(mp3Path)}");
        sb.AppendLine();
        foreach (var e in entries)
            sb.AppendLine($"**[{e.TimeLabel}] {e.Speaker}:** {e.Text}").AppendLine();
        File.WriteAllText(TranscriptPath(mp3Path), sb.ToString(), Encoding.UTF8);
    }

    private static string TranscriptPath(string mp3Path) => mp3Path[..^4] + ".транскрипт.md";
    private static string SummaryPath(string mp3Path) => mp3Path[..^4] + ".итоги.md";

    private async void MakeSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_finalTranscript is not { Count: > 0 } transcript || _lastFile is null) return;
        if (OllamaModels.SelectedItem is not string model || string.IsNullOrWhiteSpace(model))
        {
            Status.Text = "Выберите модель Ollama.";
            return;
        }

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        var ct = _summaryCts.Token;

        MakeSummaryBtn.IsEnabled = false;
        RightTabs.SelectedIndex = 1;
        SummaryBox.Clear();
        Status.Text = $"Формирую итоги ({model})…";

        try
        {
            var sb = new StringBuilder();
            await foreach (var chunk in _ollama.ChatStreamAsync(
                model, SummaryPrompt.System, SummaryPrompt.BuildUserMessage(transcript), ct))
            {
                sb.Append(chunk);
                SummaryBox.AppendText(chunk);
                SummaryBox.ScrollToEnd();
            }

            File.WriteAllText(SummaryPath(_lastFile), sb.ToString(), Encoding.UTF8);
            Status.Text = $"Итоги сохранены: {Path.GetFileName(SummaryPath(_lastFile))}";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Формирование итогов отменено.";
        }
        catch (Exception ex) when (ex is OllamaUnavailableException or OllamaModelMissingException)
        {
            Status.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Status.Text = $"Ошибка итогов: {ex.Message}";
        }
        finally
        {
            MakeSummaryBtn.IsEnabled = true;
        }
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (SummaryBox.Text.Length > 0) Clipboard.SetText(SummaryBox.Text);
    }

    /// <summary>Переключает окно между состояниями «идёт запись» и «ожидание».</summary>
    private void SetRecordingUi(bool recording)
    {
        TaskbarInfo.Overlay = recording
            ? (System.Windows.Media.ImageSource)FindResource("RecOverlayIcon")
            : null;
        StopIcon.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecDot.Visibility = recording ? Visibility.Visible : Visibility.Hidden;
        Hint.Text = recording ? "идёт запись — нажмите, чтобы остановить" : "нажмите, чтобы начать запись";
        RenderDevices.IsEnabled = CaptureDevices.IsEnabled = SettingsPanel.IsEnabled = !recording;
        if (recording) OpenFolderBtn.Visibility = Visibility.Collapsed;

        var pulse = (Storyboard)Resources["Pulse"];
        if (recording) pulse.Begin(this, true);
        else pulse.Stop(this);
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (_engine is null) return;

        if (_engine.Error is { } err)
        {
            Status.Text = $"Ошибка записи: {err.Message}";
            _ = StopRecordingAsync();
            return;
        }
        if (_stt?.Error is { } sttErr)
        {
            Status.Text = $"Транскрипция остановлена: {sttErr.Message}";
        }

        Timer.Text = _engine.Elapsed.ToString(@"hh\:mm\:ss");

        _sysVu = Math.Max(_engine.SystemPeak, _sysVu - 0.05);
        _micVu = Math.Max(_engine.MicPeak, _micVu - 0.05);
        SystemVu.Value = Math.Min(1, _sysVu);
        MicVu.Value = Math.Min(1, _micVu);
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_engine is null) return;
        _engine.SystemGain = (float)SystemGain.Value;
        _engine.MicGain = (float)MicGain.Value;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = OutputFolder.Text };
        if (dialog.ShowDialog(this) == true)
            OutputFolder.Text = dialog.FolderName;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile is not null && File.Exists(_lastFile))
            Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Элемент списка устройств: показывает имя, хранит MMDevice.</summary>
    private sealed record DeviceItem(MMDevice Device)
    {
        public override string ToString() => Device.FriendlyName;
    }
}
