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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CallAudioRecorder.Models;
using CallAudioRecorder.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace CallAudioRecorder;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Окно WPF освобождает движок, распознавание и клиент Ollama в обработчике Closed; " +
                    "IDisposable у окна никто бы не вызвал")]
public partial class MainWindow : Window
{
    private const string DefaultOllamaModel = "qwen3.5:9b";

    /// <summary>
    /// Границы высоты поля глоссария. Минимум — ровно три строки по 16 px плюс рамка:
    /// при 46 px третья строка резалась пополам и поле читалось как сломанное.
    /// </summary>
    private const double GlossaryMinHeight = 50, GlossaryMaxHeight = 320;

    /// <summary>Сколько высоты остаётся ленте реплик, сколько бы ни тянули глоссарий.</summary>
    private const double MinFeedHeight = 160;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<TranscriptEntry> _transcript = new();

    /// <summary>Реплики как их выдал распознаватель, по времени: из них пересобирается лента.</summary>
    private readonly List<TranscriptEntry> _recognized = new();
    private readonly OllamaClient _ollama = new();

    /// <summary>Язык записи до первого выбора — тот же, что у интерфейса; сохранённый выбор важнее.</summary>
    private LanguageProfile _language = Loc.English ? Languages.English : Languages.Russian;

    /// <summary>
    /// Язык распознавания, выбранный в списке вручную; null — не выбирали, и он следует
    /// за языком интерфейса (в том числе после переключения кнопкой EN/RU). Раньше язык
    /// писался в settings.json при любом сохранении настроек и закреплялся за первым
    /// значением: английский интерфейс оставался с русским распознаванием.
    /// </summary>
    private string? _speechLanguageChoice;

    /// <summary>
    /// Модели, выбранные в прежнем окне до смены языка. Список моделей в новом окне
    /// приходит асинхронно, уже после <see cref="AdoptSession"/>, — выбор применяется при заполнении.
    /// </summary>
    private string? _adoptedTranscriptModel, _adoptedSummaryModel;

    /// <summary>
    /// Язык интерфейса, выбранный кнопкой в заголовке: «Russian»/«English», null — не выбирали,
    /// решает язык Windows. Пока выбора нет, в settings.json и не пишем ничего своего: иначе
    /// первый же SaveSettings навсегда закрепил бы язык машины, на которой приложение запустили.
    /// </summary>
    private string? _uiLanguageChoice;

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

        // Окно тянется, но стартовая высота при крупном системном масштабе может не влезть
        // в рабочую область. Подрезаем только старт: MaxHeight здесь мешал бы растянуть окно
        // на второй монитор. Карточки настроек уходят в прокрутку, а блок записи
        // (таймер и кнопка) остаётся целым при любой высоте.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);

        OutputFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        TranscriptList.ItemsSource = _transcript;
        LoadSettings();

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _uiTimer.Tick += UiTimer_Tick;

        LoadDevices();
        LoadSystemSources();
        LoadLanguages();
        _ = LoadOllamaModelsAsync();

        Closed += (_, _) =>
        {
            _stt?.Dispose();
            _engine?.Dispose();
            _enumerator.Dispose();
            _ollama.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Окно без системного обрамления: края приходится отдавать вручную, иначе
        // ResizeMode="CanResize" не даёт ни курсора над краем, ни самого захвата.
        WindowResizeBorder.Attach(this);
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

    /// <summary>
    /// Заполняет список источников системного звука: всё устройство вывода либо одно приложение.
    /// Выбор по возможности сохраняется — список пересобирается каждый раз, когда его открывают.
    /// </summary>
    private void LoadSystemSources()
    {
        int? current = (SystemSource.SelectedItem as SystemSourceItem)?.Window?.ProcessId;

        SystemSource.Items.Clear();
        var wholeDevice = new SystemSourceItem(null);
        SystemSource.Items.Add(wholeDevice);
        foreach (var window in AudioWindows.Enumerate())
            SystemSource.Items.Add(new SystemSourceItem(window));

        SystemSource.SelectedItem = SystemSource.Items.OfType<SystemSourceItem>()
            .FirstOrDefault(item => current is not null && item.Window?.ProcessId == current) ?? wholeDevice;
    }

    private void SystemSource_DropDownOpened(object sender, EventArgs e) => LoadSystemSources();

    /// <summary>Заполняет список языков распознавания и выбирает сохранённый в настройках.</summary>
    private void LoadLanguages()
    {
        LanguageBox.Items.Clear();
        foreach (var profile in Languages.All) LanguageBox.Items.Add(profile);
        LanguageBox.SelectedItem = _language;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox?.SelectedItem is not LanguageProfile profile) return;
        _language = profile;
        // До загрузки окна список заполняется программно — это ещё не выбор пользователя.
        if (IsLoaded)
        {
            _speechLanguageChoice = profile.Language.ToString();
            SaveSettings();
        }

        // Модель другого языка качается при первой записи — предупреждаем заранее, объём заметный.
        if (!ModelDownloader.ModelsPresent(profile))
            SetStatus(Loc.T(
                $"Язык: {profile.DisplayName}. Модель (~{ModelDownloader.MissingMegabytes(profile)} МБ) " +
                "будет загружена при старте записи.",
                $"Language: {profile.DisplayName}. The model (~{ModelDownloader.MissingMegabytes(profile)} MB) " +
                "will be downloaded when recording starts."));
        else
            SetStatus(Loc.T($"Язык распознавания: {profile.DisplayName}", $"Speech language: {profile.DisplayName}"));
    }

    /// <summary>Жив ли процесс: PID из списка мог протухнуть, пока пользователь выбирал.</summary>
    private static bool ProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void SystemSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RenderDevices is null) return; // событие прилетает и во время InitializeComponent
        // Когда звук берётся из приложения, устройство вывода ни на что не влияет.
        bool wholeDevice = (SystemSource.SelectedItem as SystemSourceItem)?.Window is null;
        RenderDevices.IsEnabled = wholeDevice && _engine is null;
    }

    /// <summary>
    /// Заполняет оба списка моделей — правки транскрипта и итогов. Список один и тот же,
    /// а выбор независимый: коррекции хватает лёгкой text-модели, итогам нужна та,
    /// что лучше рассуждает.
    /// </summary>
    private async Task LoadOllamaModelsAsync()
    {
        try
        {
            var models = await _ollama.ListModelsAsync();
            FillModels(TranscriptModels, models, _adoptedTranscriptModel);
            FillModels(OllamaModels, models, _adoptedSummaryModel);
        }
        catch (OllamaUnavailableException)
        {
            // Ollama может подняться и позже — оставляем модель по умолчанию, чтобы было что выбрать.
            FillModels(TranscriptModels, new[] { DefaultOllamaModel }, _adoptedTranscriptModel);
            FillModels(OllamaModels, new[] { DefaultOllamaModel }, _adoptedSummaryModel);
        }
    }

    /// <param name="preferred">Модель, выбранная в прежнем окне до смены языка; null — по умолчанию.</param>
    private static void FillModels(ComboBox box, string[] models, string? preferred)
    {
        box.Items.Clear();
        foreach (var m in models) box.Items.Add(m);
        box.SelectedItem = (preferred is not null && models.Contains(preferred) ? preferred : null)
                           ?? models.FirstOrDefault(m => m.StartsWith(DefaultOllamaModel, StringComparison.Ordinal))
                           ?? (models.Length > 0 ? models[0] : null);
    }

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_engine is null) await StartRecordingAsync();
        else await StopRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        var sourceWindow = (SystemSource.SelectedItem as SystemSourceItem)?.Window;
        if (CaptureDevices.SelectedItem is not DeviceItem capture)
        {
            SetStatus(Loc.T("Выберите микрофон.", "Pick a microphone."), StatusKind.Error);
            return;
        }
        if (sourceWindow is null && RenderDevices.SelectedItem is not DeviceItem)
        {
            SetStatus(Loc.T("Выберите устройство вывода или приложение-источник.",
                "Pick an output device or a source app."), StatusKind.Error);
            return;
        }

        // Окно могло закрыться, пока список висел на экране, — PID стал бы чужим.
        if (sourceWindow is not null && !ProcessAlive(sourceWindow.ProcessId))
        {
            SetStatus(Loc.T($"Приложение «{sourceWindow.ProcessName}» уже закрыто. Обновите список источников.",
                $"“{sourceWindow.ProcessName}” has already exited. Refresh the source list."), StatusKind.Error);
            LoadSystemSources();
            return;
        }

        var folder = OutputFolder.Text.Trim();
        if (!Directory.Exists(folder))
        {
            SetStatus(Loc.T($"Папка не найдена: {folder}", $"Folder not found: {folder}"), StatusKind.Error);
            return;
        }

        bool transcribe = TranscribeCheck.IsChecked == true;
        _busy = true;
        try
        {
            if (transcribe && !ModelDownloader.ModelsPresent(_language))
            {
                var progress = new Progress<string>(s => SetStatus(s, StatusKind.Progress));
                BeginTask(determinate: false);
                try
                {
                    await ModelDownloader.EnsureAsync(progress, _language);
                }
                catch (Exception ex)
                {
                    SetStatus(Loc.T($"Модели не загрузились: {ex.Message} Запись без транскрипта.",
                        $"Models failed to download: {ex.Message} Recording without a transcript."), StatusKind.Error);
                    transcribe = false;
                }
                finally
                {
                    EndTask();
                }
            }

            int bitrate = Br128.IsChecked == true ? 128 : Br320.IsChecked == true ? 320 : 192;
            var path = Path.Combine(folder,
                $"{Loc.T("Запись", "Recording")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");

            try
            {
                var renderDevice = sourceWindow is null ? ((DeviceItem)RenderDevices.SelectedItem).Device : null;
                _engine = new RecordingEngine(renderDevice, capture.Device, path, bitrate, transcribe,
                    sourceWindow?.ProcessId)
                {
                    SystemGain = (float)SystemGain.Value,
                    MicGain = (float)MicGain.Value
                };

                if (transcribe)
                {
                    SetStatus(Loc.T("Загружаю модель распознавания…", "Loading the speech model…"), StatusKind.Progress);
                    var engine = _engine;
                    var hotwords = TranscriptCorrector.ToHotwords(GlossaryBox.Text, _language);
                    // Глоссарий работает с двух сторон: hotwords смещают распознавание к нужным
                    // словам, канонизатор приводит к канону то, что оно всё-таки исказило.
                    var canonizer = TermCanonizer.Build(GlossaryBox.Text);
                    bool diarize = DiarizeCheck.IsChecked == true;
                    var language = _language;
                    _stt = await Task.Run(() => new TranscriptionService(language, hotwords, diarize, canonizer));
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
                SetStatus(Loc.T($"Не удалось начать запись: {ex.Message}", $"Could not start recording: {ex.Message}"),
                    StatusKind.Error);
                return;
            }

            _transcript.Clear();
            _recognized.Clear();
            _finalTranscript = null;
            MakeSummaryBtn.IsEnabled = false;
            SetTranscriptButtons(false);
            TranscriptEmpty.Visibility = Visibility.Visible;

            _uiTimer.Start();
            SetRecordingUi(true);
            SetStatus(Loc.T($"Идёт запись: {Path.GetFileName(path)}", $"Recording: {Path.GetFileName(path)}") +
                      (transcribe ? Loc.T(" · транскрипция включена", " · transcription on") : ""),
                StatusKind.Progress);
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
                SetStatus(Loc.T("Распознаю остаток речи…", "Recognizing the remaining speech…"), StatusKind.Progress);
                _finalTranscript = await Task.Run(stt.StopAndDrain);
                stt.Dispose();
                _stt = null;

                if (_finalTranscript.Count > 0)
                {
                    // Живая лента несёт метки, выданные по ходу записи, а после неё диаризация
                    // пересобрала профили: без замены окно показывало десяток «собеседников»,
                    // когда в файле их трое.
                    ShowBlocks(_finalTranscript);
                    SaveTranscript(path, _finalTranscript);
                    MakeSummaryBtn.IsEnabled = true;
                    SetTranscriptButtons(true);
                }
            }

            var size = new FileInfo(path).Length / 1024.0 / 1024.0;
            var extra = _finalTranscript is { Count: > 0 }
                ? Loc.T($" · реплик: {_finalTranscript.Count}", $" · utterances: {_finalTranscript.Count}") : "";
            SetStatus(Loc.T($"Сохранено: {Path.GetFileName(path)} ({size:F1} МБ){extra}",
                $"Saved: {Path.GetFileName(path)} ({size:F1} MB){extra}"), StatusKind.Done);
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

    /// <summary>
    /// Тянет нижний край поля глоссария. Высоту забираем у ленты реплик, но не всю:
    /// иначе на подрезанном по WorkArea окне транскрипт схлопывается в полоску.
    /// </summary>
    private void GlossaryGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double current = double.IsNaN(GlossaryBox.Height) ? GlossaryBox.ActualHeight : GlossaryBox.Height;
        double room = current + TranscriptScroll.ActualHeight - MinFeedHeight;
        double max = Math.Max(GlossaryMinHeight, Math.Min(GlossaryMaxHeight, room));
        GlossaryBox.Height = Math.Clamp(current + e.VerticalChange, GlossaryMinHeight, max);
    }

    private void GlossaryGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveSettings();

    /// <summary>Двойной щелчок по ручке возвращает поле к исходным двум строкам.</summary>
    private void GlossaryGrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        GlossaryBox.Height = GlossaryMinHeight;
        SaveSettings();
    }

    private void OwnerName_LostFocus(object sender, RoutedEventArgs e) => SaveSettings();

    private void Diarize_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings(); // не писать на диск во время InitializeComponent
    }

    /// <summary>
    /// Меняет метки «Собеседник N» на имена, прозвучавшие в разговоре. Отдельный шаг
    /// от коррекции текста: имена нужны не всегда, а стоят целого запроса к модели.
    /// </summary>
    private async void NameSpeakers_Click(object sender, RoutedEventArgs e)
    {
        if (!TryStartTranscriptEdit(out var transcript, out var model)) return;

        // В списке задач «Собеседник 12» бесполезен, а имя участника почти всегда
        // звучит в самом разговоре.
        SetStatus(Loc.T($"Определяю имена участников ({model})…", $"Naming the speakers ({model})…"),
            StatusKind.Progress);
        BeginTask(determinate: false);
        try
        {
            var named = await SpeakerNamer.ApplyNamesAsync(
                _ollama, model, transcript, _language, OwnerNameBox.Text, _summaryCts!.Token);

            // Сколько меток стало именами: модель могла не найти ничего надёжного,
            // и тогда реплики возвращаются как были.
            var left = named.Select(entry => entry.Speaker).ToHashSet(StringComparer.Ordinal);
            int renamed = transcript.Select(entry => entry.Speaker).Distinct(StringComparer.Ordinal)
                .Count(label => !Languages.IsMeLabel(label) && !left.Contains(label));

            ApplyEditedTranscript(named);
            if (renamed > 0)
                SetStatus(Loc.T($"Участники определены · имён: {renamed}", $"Speakers named · names: {renamed}"),
                    StatusKind.Done);
            else
                SetStatus(Loc.T("Имена участников не определились — метки остались как были.",
                    "No speaker names found — the labels are left as they were."));
        }
        catch (Exception ex)
        {
            ShowEditError(ex, Loc.T("Определение участников отменено.", "Naming speakers cancelled."),
                Loc.T("Ошибка определения участников", "Naming speakers failed"));
        }
        finally
        {
            FinishTranscriptEdit();
        }
    }

    /// <summary>Прогоняет транскрипт через LLM: восстанавливает английские термины и чинит ошибки распознавания.</summary>
    private async void FixText_Click(object sender, RoutedEventArgs e)
    {
        if (!TryStartTranscriptEdit(out var transcript, out var model)) return;

        SetStatus(Loc.T($"Исправляю текст ({model})…", $"Fixing the text ({model})…"), StatusKind.Progress);
        BeginTask(determinate: true);
        try
        {
            // Длинный транскрипт корректор гонит партиями — показываем, сколько реплик уже прошло.
            int total = transcript.Count;
            var progress = new Progress<int>(done =>
            {
                TaskBar.Value = (double)done / total;
                SetStatus(Loc.T($"Исправляю текст ({model}): {done} из {total}…",
                    $"Fixing the text ({model}): {done} of {total}…"), StatusKind.Progress);
            });
            var fixedEntries = await TranscriptCorrector.CorrectAsync(
                _ollama, model, transcript, GlossaryBox.Text, progress, _language, _summaryCts!.Token);

            ApplyEditedTranscript(fixedEntries);
            SetStatus(Loc.T($"Текст исправлен · реплик: {fixedEntries.Count}",
                $"Text fixed · utterances: {fixedEntries.Count}"), StatusKind.Done);
        }
        catch (Exception ex)
        {
            ShowEditError(ex, Loc.T("Исправление отменено.", "Text fixing cancelled."),
                Loc.T("Ошибка исправления", "Text fixing failed"));
        }
        finally
        {
            FinishTranscriptEdit();
        }
    }

    /// <summary>
    /// Общее начало обоих шагов правки: есть ли что править и чем, отмена предыдущей
    /// работы с LLM и блокировка кнопок.
    /// </summary>
    private bool TryStartTranscriptEdit(out IReadOnlyList<TranscriptEntry> transcript, out string model)
    {
        transcript = Array.Empty<TranscriptEntry>();
        model = "";
        if (_finalTranscript is not { Count: > 0 } entries || _lastFile is null) return false;
        if (TranscriptModels.SelectedItem is not string selected || string.IsNullOrWhiteSpace(selected))
        {
            SetStatus(Loc.T("Выберите модель Ollama для правки транскрипта.",
                "Pick an Ollama model for editing the transcript."), StatusKind.Error);
            return false;
        }

        transcript = entries;
        model = selected;

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        SetTranscriptButtons(false);
        MakeSummaryBtn.IsEnabled = false;
        return true;
    }

    /// <summary>Показывает результат шага правки в ленте и перезаписывает <c>.транскрипт.md</c>.</summary>
    private void ApplyEditedTranscript(IReadOnlyList<TranscriptEntry> entries)
    {
        _finalTranscript = entries;
        _transcript.Clear();
        foreach (var entry in entries) _transcript.Add(entry);
        TranscriptScroll.ScrollToEnd();
        SaveTranscript(_lastFile!, entries);
    }

    private void ShowEditError(Exception ex, string cancelled, string prefix)
    {
        if (ex is OperationCanceledException) SetStatus(cancelled);
        else if (ex is OllamaUnavailableException or OllamaModelMissingException)
            SetStatus(ex.Message, StatusKind.Error);
        else SetStatus($"{prefix}: {ex.Message}", StatusKind.Error);
    }

    private void FinishTranscriptEdit()
    {
        EndTask();
        SetTranscriptButtons(true);
        MakeSummaryBtn.IsEnabled = true;
    }

    /// <summary>Обе кнопки правки гаснут и включаются вместе: шаги идут по одному транскрипту.</summary>
    private void SetTranscriptButtons(bool enabled) =>
        NameSpeakersBtn.IsEnabled = FixTextBtn.IsEnabled = enabled;

    /// <summary>Чем именно является сообщение статуса: от этого иконка и цвет.</summary>
    private enum StatusKind
    {
        /// <summary>Нейтральное сообщение: что выбрано, что готово к работе.</summary>
        Info,
        /// <summary>Идёт длинная работа — запись, докачка модели, запрос к LLM.</summary>
        Progress,
        /// <summary>Работа закончена успешно, файл на диске.</summary>
        Done,
        /// <summary>Что-то не вышло. Раньше ошибка выглядела ровно как успех.</summary>
        Error
    }

    private void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        Status.Text = text;
        var (glyph, brush) = kind switch
        {
            StatusKind.Progress => ("\uE895", "MutedBrush"),   // Sync
            StatusKind.Done => ("\uE73E", "GreenBrush"),       // CheckMark
            StatusKind.Error => ("\uE783", "AccentBrush"),     // Error
            _ => ("\uE930", "MutedBrush")                      // Info
        };
        StatusIcon.Text = glyph;
        var colour = (System.Windows.Media.Brush)FindResource(brush);
        StatusIcon.Foreground = colour;
        Status.Foreground = kind == StatusKind.Info ? (System.Windows.Media.Brush)FindResource("MutedBrush") : colour;
    }

    // ── 2. Длинная работа LLM: полоса прогресса и отмена ──

    /// <summary>
    /// Показывает полосу задачи. <paramref name="determinate"/> = известно ли, сколько
    /// работы всего: коррекция знает число реплик, имена и итоги — нет.
    /// </summary>
    private void BeginTask(bool determinate)
    {
        TaskBar.IsIndeterminate = !determinate;
        TaskBar.Value = 0;
        CancelTaskBtn.IsEnabled = true;
        TaskBar.Visibility = Visibility.Visible;
        CancelTaskBtn.Visibility = Visibility.Visible;
    }

    private void EndTask()
    {
        TaskBar.Visibility = Visibility.Collapsed;
        CancelTaskBtn.Visibility = Visibility.Collapsed;
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        CancelTaskBtn.IsEnabled = false; // повторный клик уже ничего не добавит
        _summaryCts?.Cancel();
    }

    /// <summary>Свёрнут ли блок настроек вкладки — состояние переживает перезапуск.</summary>
    private void TranscriptSettings_Toggled(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) SaveSettings();
    }

    /// <summary>
    /// Переключает язык интерфейса. Надписи XAML берутся один раз при загрузке разметки,
    /// поэтому окно пересоздаётся — на том же месте и с той же последней записью: транскрипт
    /// и итоги на экране терять незачем.
    /// </summary>
    private void UiLanguage_Click(object sender, RoutedEventArgs e)
    {
        // Закрытие окна оборвало бы и запись, и работу модели — их надо сперва закончить.
        if (_engine is not null || _busy || TaskBar.Visibility == Visibility.Visible)
        {
            SetStatus(Loc.T("Язык интерфейса можно сменить, когда закончатся запись и работа модели.",
                "The interface language can be changed once recording and model work are finished."),
                StatusKind.Error);
            return;
        }

        Loc.English = !Loc.English;
        _uiLanguageChoice = Loc.English ? "English" : "Russian";
        SaveSettings();

        var next = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left,
            Top = Top,
            Width = Width,
            Height = Height
        };
        next.AdoptSession(this);
        Application.Current.MainWindow = next;
        next.Show();
        Close();
    }

    /// <summary>
    /// Переносит в новое окно то, что не лежит в settings.json: источники, модели, папку,
    /// микшер и последнюю запись. Без этого новое окно молча возвращалось к системным
    /// умолчаниям — и следующая запись шла не с тем микрофоном и не из того приложения.
    /// </summary>
    private void AdoptSession(MainWindow old)
    {
        // Устройства и окна — по идентификатору: элементы списков в новом окне свои.
        SelectDevice(RenderDevices, old.RenderDevices);
        SelectDevice(CaptureDevices, old.CaptureDevices);
        if ((old.SystemSource.SelectedItem as SystemSourceItem)?.Window is { } window)
        {
            // Приложение могло закрыться — тогда остаётся «всё устройство вывода».
            var same = SystemSource.Items.OfType<SystemSourceItem>()
                .FirstOrDefault(item => item.Window?.ProcessId == window.ProcessId);
            if (same is not null) SystemSource.SelectedItem = same;
        }

        _adoptedTranscriptModel = old.TranscriptModels.SelectedItem as string;
        _adoptedSummaryModel = old.OllamaModels.SelectedItem as string;
        // Если список моделей уже успел заполниться, выбор применяем сразу.
        if (_adoptedTranscriptModel is not null && TranscriptModels.Items.Contains(_adoptedTranscriptModel))
            TranscriptModels.SelectedItem = _adoptedTranscriptModel;
        if (_adoptedSummaryModel is not null && OllamaModels.Items.Contains(_adoptedSummaryModel))
            OllamaModels.SelectedItem = _adoptedSummaryModel;

        OutputFolder.Text = old.OutputFolder.Text;
        Br128.IsChecked = old.Br128.IsChecked;
        Br192.IsChecked = old.Br192.IsChecked;
        Br320.IsChecked = old.Br320.IsChecked;
        SystemGain.Value = old.SystemGain.Value;
        MicGain.Value = old.MicGain.Value;
        TranscribeCheck.IsChecked = old.TranscribeCheck.IsChecked;
        RightTabs.SelectedIndex = old.RightTabs.SelectedIndex;

        _lastFile = old._lastFile;
        OpenFolderBtn.Visibility = old.OpenFolderBtn.Visibility;
        if (old._finalTranscript is { Count: > 0 } transcript)
        {
            _finalTranscript = transcript;
            ShowBlocks(transcript);
            SetTranscriptButtons(true);
            MakeSummaryBtn.IsEnabled = true;
        }
        SummaryBox.Text = old.SummaryBox.Text;
        SetStatus(Loc.T("Язык интерфейса: русский.", "Interface language: English."));
    }

    /// <summary>Выбирает в <paramref name="box"/> то же устройство, что выбрано в <paramref name="from"/>.</summary>
    private static void SelectDevice(ComboBox box, ComboBox from)
    {
        if (from.SelectedItem is not DeviceItem selected) return;
        var same = box.Items.OfType<DeviceItem>().FirstOrDefault(item => item.Device.ID == selected.Device.ID);
        if (same is not null) box.SelectedItem = same;
    }

    /// <summary>
    /// Заполняет окно образцом данных для <c>--ui-shot</c>: лента реплик, полоса длинной
    /// задачи и статус-ошибка. Без этого снимок всегда показывает пустое окно, и вёрстку
    /// ленты приходится проверять живой записью.
    /// </summary>
    internal void FillLayoutSample()
    {
        // Образец на языке интерфейса: английские надписи длиннее, и вёрстку надо видеть
        // с настоящими английскими репликами, а не с русскими в английском окне.
        var sample = Loc.English ? new[]
        {
            new TranscriptEntry("Speaker 1", "Let us start with the release. Build 1.8.3 went to QA last night, " +
                                             "no critical crashes so far.", TimeSpan.FromSeconds(12)),
            new TranscriptEntry("Me", "What about the interstitials? Last week they failed to load on some devices.",
                                TimeSpan.FromSeconds(31)),
            new TranscriptEntry("Speaker 2", "Fixed, it was the AppLovin config. Fill rate is back to normal now.",
                                TimeSpan.FromSeconds(44)),
            new TranscriptEntry("Speaker 3", "Over to Marina, she has the retention numbers.",
                                TimeSpan.FromSeconds(78)),
            new TranscriptEntry("Speaker 4", "Day two retention holds at forty-two percent, three points higher " +
                                             "than the previous build.", TimeSpan.FromSeconds(85)),
            new TranscriptEntry("Me", "Great. Then we ship the Battle Pass in the next sprint.",
                                TimeSpan.FromSeconds(140)),
        } : new[]
        {
            new TranscriptEntry("Собеседник 1", "Давайте начнём с релиза. Сборка 1.8.3 ушла в тест вчера вечером, " +
                                                "критичных падений пока нет.", TimeSpan.FromSeconds(12)),
            new TranscriptEntry("Я", "А что с интерстишлами? На прошлой неделе они не грузились на части устройств.",
                                TimeSpan.FromSeconds(31)),
            new TranscriptEntry("Собеседник 2", "Починили, дело было в конфиге AppLovin. Сейчас fill rate вернулся " +
                                               "к обычным значениям.", TimeSpan.FromSeconds(44)),
            new TranscriptEntry("Собеседник 3", "Передаю слово Марине — у неё цифры по ретеншну.",
                                TimeSpan.FromSeconds(78)),
            new TranscriptEntry("Собеседник 4", "День второй держится на сорока двух процентах, это на три пункта " +
                                               "выше, чем в прошлом билде.", TimeSpan.FromSeconds(85)),
            new TranscriptEntry("Я", "Отлично. Тогда собираем Battle Pass к следующему спринту.",
                                TimeSpan.FromSeconds(140)),
        };
        _transcript.Clear();
        foreach (var entry in sample) _transcript.Add(entry);
        TranscriptEmpty.Visibility = Visibility.Collapsed;

        SetTranscriptButtons(true);
        MakeSummaryBtn.IsEnabled = true;

        // Прогоняем и неопределённый режим: его пульсацию запускает триггер шаблона,
        // и сломанный Storyboard выстрелил бы только в момент переключения — то есть
        // у пользователя посреди работы модели, а не на сборке.
        BeginTask(determinate: false);
        BeginTask(determinate: true);
        TaskBar.Value = 0.42;
        SetStatus(Loc.T("Исправляю текст (qwen3.5:9b): 220 из 526…", "Fixing the text (qwen3.5:9b): 220 of 526…"),
            StatusKind.Progress);
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
            if (doc.RootElement.TryGetProperty("glossaryHeight", out var gh))
                GlossaryBox.Height = Math.Clamp(gh.GetDouble(), GlossaryMinHeight, GlossaryMaxHeight);
            if (doc.RootElement.TryGetProperty("ownerName", out var owner))
                OwnerNameBox.Text = owner.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("transcriptSettingsOpen", out var open))
                TranscriptSettingsToggle.IsChecked = open.GetBoolean();
            if (doc.RootElement.TryGetProperty("diarizeSpeakers", out var di))
                DiarizeCheck.IsChecked = di.GetBoolean();
            if (doc.RootElement.TryGetProperty("speechLanguage", out var speech)
                && speech.ValueKind == System.Text.Json.JsonValueKind.String)
                _speechLanguageChoice = speech.GetString();
            // Старый ключ писался при любом сохранении, и «Russian» в нём — чаще прежний
            // умолчательный язык, чем выбор. А «English» мог появиться только из списка.
            else if (doc.RootElement.TryGetProperty("language", out var legacy)
                     && legacy.ValueKind == System.Text.Json.JsonValueKind.String
                     && Languages.Parse(legacy.GetString()) == Languages.English)
                _speechLanguageChoice = Languages.English.Language.ToString();
            if (_speechLanguageChoice is not null) _language = Languages.Parse(_speechLanguageChoice);
            _uiLanguageChoice = ReadUiLanguage(doc.RootElement);
        }
        catch
        {
            // повреждённый settings.json не должен мешать запуску
        }
        TranscriptionService.BubbleMergeGap = TimeSpan.FromSeconds(MergeGapSlider.Value);
    }

    /// <summary>
    /// Выбранный кнопкой в заголовке язык интерфейса из settings.json; null — не выбирали.
    /// Нужен до создания окна: от него зависят надписи разметки (<see cref="Loc.Resolve"/>).
    /// </summary>
    internal static string? ReadSavedUiLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return ReadUiLanguage(doc.RootElement);
        }
        catch
        {
            return null; // повреждённый settings.json — пусть решает язык системы
        }
    }

    private static string? ReadUiLanguage(System.Text.Json.JsonElement root) =>
        root.TryGetProperty("uiLanguage", out var ui) && ui.ValueKind == System.Text.Json.JsonValueKind.String
            ? ui.GetString()
            : null;

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                mergeGapSeconds = MergeGapSlider.Value,
                glossary = GlossaryBox.Text,
                // Высота всегда задана явно, но NaN в JSON не сериализуется — подстрахуемся.
                glossaryHeight = double.IsNaN(GlossaryBox.Height) ? GlossaryMinHeight : GlossaryBox.Height,
                ownerName = OwnerNameBox.Text,
                diarizeSpeakers = DiarizeCheck.IsChecked == true,
                transcriptSettingsOpen = TranscriptSettingsToggle.IsChecked == true,
                speechLanguage = _speechLanguageChoice,
                uiLanguage = _uiLanguageChoice
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
        // Запись уже остановлена: реплика есть в итоговом транскрипте, а её живая метка
        // вернула бы в ленту фантома, которого консолидация уже слила.
        if (_stt is null) return;

        // Реплика встаёт на своё место по времени, а лента пересобирается: без этого ответ
        // собеседника оказывался выше вопроса, потому что распознаются каналы не по порядку.
        ShowBlocks(TranscriptionService.AppendRecognized(_recognized, entry));
    }

    /// <summary>Приводит ленту к <paramref name="blocks"/>, не трогая совпавшие пузыри.</summary>
    private void ShowBlocks(IReadOnlyList<TranscriptEntry> blocks)
    {
        // Обновляем только разошедшиеся блоки — перезаполнение всей коллекции сбрасывало бы
        // прокрутку и моргало на каждой реплике.
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i >= _transcript.Count) _transcript.Add(blocks[i]);
            else if (_transcript[i] != blocks[i]) _transcript[i] = blocks[i];
        }
        while (_transcript.Count > blocks.Count) _transcript.RemoveAt(_transcript.Count - 1);

        TranscriptEmpty.Visibility = Visibility.Collapsed;
        TranscriptScroll.ScrollToEnd();
    }

    private static void SaveTranscript(string mp3Path, IReadOnlyList<TranscriptEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine((RussianNames(mp3Path) ? "# Транскрипт: " : "# Transcript: ") +
                      Path.GetFileNameWithoutExtension(mp3Path));
        sb.AppendLine();
        foreach (var e in entries)
            sb.AppendLine($"**[{e.TimeLabel}] {e.Speaker}:** {e.Text}").AppendLine();
        File.WriteAllText(TranscriptPath(mp3Path), sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Русские ли имена у файлов этой записи. Решает имя самого mp3, а не текущий язык
    /// интерфейса: смена языка переносит последнюю запись в новое окно (<see cref="AdoptSession"/>),
    /// и по текущему языку правка записи «Запись_X» ушла бы в новый «Запись_X.transcript.md»,
    /// оставив исходный «.транскрипт.md» неисправленным.
    /// </summary>
    private static bool RussianNames(string mp3Path) =>
        Path.GetFileName(mp3Path).StartsWith("Запись_", StringComparison.Ordinal);

    private static string TranscriptPath(string mp3Path) =>
        mp3Path[..^4] + (RussianNames(mp3Path) ? ".транскрипт.md" : ".transcript.md");

    private static string SummaryPath(string mp3Path) =>
        mp3Path[..^4] + (RussianNames(mp3Path) ? ".итоги.md" : ".summary.md");

    private async void MakeSummary_Click(object sender, RoutedEventArgs e)
    {
        if (_finalTranscript is not { Count: > 0 } transcript || _lastFile is null) return;
        if (OllamaModels.SelectedItem is not string model || string.IsNullOrWhiteSpace(model))
        {
            SetStatus(Loc.T("Выберите модель Ollama.", "Pick an Ollama model."), StatusKind.Error);
            return;
        }

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        var ct = _summaryCts.Token;

        MakeSummaryBtn.IsEnabled = false;
        RightTabs.SelectedIndex = 1;
        SummaryBox.Clear();
        SetStatus(Loc.T($"Формирую итоги ({model})…", $"Making the summary ({model})…"), StatusKind.Progress);
        BeginTask(determinate: false);

        try
        {
            var sb = new StringBuilder();
            var outcome = new ChatOutcome();
            // Длинная встреча не влезает в окно локальной модели — SummaryComposer идёт по частям
            // и сообщает через stage, на какой он сейчас.
            var stage = new Progress<string>(text => SetStatus($"{text} ({model})", StatusKind.Progress));
            await foreach (var chunk in SummaryComposer.ComposeAsync(
                _ollama, model, transcript, stage, outcome, _language, GlossaryBox.Text, ct))
            {
                sb.Append(chunk);
                SummaryBox.AppendText(chunk);
                SummaryBox.ScrollToEnd();
            }

            File.WriteAllText(SummaryPath(_lastFile), sb.ToString(), Encoding.UTF8);
            if (outcome.HitContextLimit)
                SetStatus(Loc.T(
                    $"Итоги оборвались: модель «{model}» упёрлась в окно контекста или в предел длины ответа. " +
                    $"Сохранено: {Path.GetFileName(SummaryPath(_lastFile))}",
                    $"The summary was cut off: “{model}” hit its context window or the reply length limit. " +
                    $"Saved: {Path.GetFileName(SummaryPath(_lastFile))}"), StatusKind.Error);
            else
                SetStatus(Loc.T($"Итоги сохранены: {Path.GetFileName(SummaryPath(_lastFile))}",
                    $"Summary saved: {Path.GetFileName(SummaryPath(_lastFile))}"), StatusKind.Done);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Loc.T("Формирование итогов отменено.", "Summary cancelled."));
        }
        catch (Exception ex) when (ex is OllamaUnavailableException or OllamaModelMissingException)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T($"Ошибка итогов: {ex.Message}", $"Summary failed: {ex.Message}"), StatusKind.Error);
        }
        finally
        {
            EndTask();
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
        Hint.Text = recording
            ? Loc.T("идёт запись — нажмите, чтобы остановить", "recording — click to stop")
            : Loc.T("нажмите, чтобы начать запись", "click to start recording");
        SystemSource.IsEnabled = CaptureDevices.IsEnabled = SettingsPanel.IsEnabled = !recording;
        UiLanguageBtn.IsEnabled = !recording;
        LanguageBox.IsEnabled = !recording;
        RenderDevices.IsEnabled = !recording && (SystemSource.SelectedItem as SystemSourceItem)?.Window is null;
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
            SetStatus(Loc.T($"Ошибка записи: {err.Message}", $"Recording error: {err.Message}"), StatusKind.Error);
            _ = StopRecordingAsync();
            return;
        }
        if (_stt?.Error is { } sttErr)
        {
            SetStatus(Loc.T($"Транскрипция остановлена: {sttErr.Message}", $"Transcription stopped: {sttErr.Message}"),
                StatusKind.Error);
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

    /// <summary>Источник системного звука: всё устройство вывода (Window = null) или окно приложения.</summary>
    private sealed record SystemSourceItem(AudioWindow? Window)
    {
        public override string ToString() => Window?.ToString() ?? Loc.T("Всё устройство вывода", "Whole output device");
    }
}
