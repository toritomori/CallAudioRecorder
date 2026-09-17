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

    private LanguageProfile _language = Languages.Russian;
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
        if (IsLoaded) SaveSettings();

        // Модель другого языка качается при первой записи — предупреждаем заранее, объём заметный.
        if (!ModelDownloader.ModelsPresent(profile))
            SetStatus($"Язык: {profile.DisplayName}. Модель (~{ModelDownloader.MissingMegabytes(profile)} МБ) " +
                      "будет загружена при старте записи.");
        else
            SetStatus($"Язык распознавания: {profile.DisplayName}");
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
            FillModels(TranscriptModels, models);
            FillModels(OllamaModels, models);
        }
        catch (OllamaUnavailableException)
        {
            // Ollama может подняться и позже — оставляем модель по умолчанию, чтобы было что выбрать.
            FillModels(TranscriptModels, new[] { DefaultOllamaModel });
            FillModels(OllamaModels, new[] { DefaultOllamaModel });
        }
    }

    private static void FillModels(ComboBox box, string[] models)
    {
        box.Items.Clear();
        foreach (var m in models) box.Items.Add(m);
        box.SelectedItem = models.FirstOrDefault(m => m.StartsWith(DefaultOllamaModel, StringComparison.Ordinal))
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
            SetStatus("Выберите микрофон.", StatusKind.Error);
            return;
        }
        if (sourceWindow is null && RenderDevices.SelectedItem is not DeviceItem)
        {
            SetStatus("Выберите устройство вывода или приложение-источник.", StatusKind.Error);
            return;
        }

        // Окно могло закрыться, пока список висел на экране, — PID стал бы чужим.
        if (sourceWindow is not null && !ProcessAlive(sourceWindow.ProcessId))
        {
            SetStatus($"Приложение «{sourceWindow.ProcessName}» уже закрыто. Обновите список источников.",
                StatusKind.Error);
            LoadSystemSources();
            return;
        }

        var folder = OutputFolder.Text.Trim();
        if (!Directory.Exists(folder))
        {
            SetStatus($"Папка не найдена: {folder}", StatusKind.Error);
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
                    SetStatus($"Модели не загрузились: {ex.Message} Запись без транскрипта.", StatusKind.Error);
                    transcribe = false;
                }
                finally
                {
                    EndTask();
                }
            }

            int bitrate = Br128.IsChecked == true ? 128 : Br320.IsChecked == true ? 320 : 192;
            var path = Path.Combine(folder, $"Запись_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp3");

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
                    SetStatus("Загружаю модель распознавания…", StatusKind.Progress);
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
                SetStatus($"Не удалось начать запись: {ex.Message}", StatusKind.Error);
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
            SetStatus($"Идёт запись: {Path.GetFileName(path)}" + (transcribe ? " · транскрипция включена" : ""),
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
                SetStatus("Распознаю остаток речи…", StatusKind.Progress);
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
                ? $" · реплик: {_finalTranscript.Count}" : "";
            SetStatus($"Сохранено: {Path.GetFileName(path)} ({size:F1} МБ){extra}", StatusKind.Done);
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
        SetStatus($"Определяю имена участников ({model})…", StatusKind.Progress);
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
            if (renamed > 0) SetStatus($"Участники определены · имён: {renamed}", StatusKind.Done);
            else SetStatus("Имена участников не определились — метки остались как были.");
        }
        catch (Exception ex)
        {
            ShowEditError(ex, "Определение участников отменено.", "Ошибка определения участников");
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

        SetStatus($"Исправляю текст ({model})…", StatusKind.Progress);
        BeginTask(determinate: true);
        try
        {
            // Длинный транскрипт корректор гонит партиями — показываем, сколько реплик уже прошло.
            int total = transcript.Count;
            var progress = new Progress<int>(done =>
            {
                TaskBar.Value = (double)done / total;
                SetStatus($"Исправляю текст ({model}): {done} из {total}…", StatusKind.Progress);
            });
            var fixedEntries = await TranscriptCorrector.CorrectAsync(
                _ollama, model, transcript, GlossaryBox.Text, progress, _language, _summaryCts!.Token);

            ApplyEditedTranscript(fixedEntries);
            SetStatus($"Текст исправлен · реплик: {fixedEntries.Count}", StatusKind.Done);
        }
        catch (Exception ex)
        {
            ShowEditError(ex, "Исправление отменено.", "Ошибка исправления");
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
            SetStatus("Выберите модель Ollama для правки транскрипта.", StatusKind.Error);
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
    /// Заполняет окно образцом данных для <c>--ui-shot</c>: лента реплик, полоса длинной
    /// задачи и статус-ошибка. Без этого снимок всегда показывает пустое окно, и вёрстку
    /// ленты приходится проверять живой записью.
    /// </summary>
    internal void FillLayoutSample()
    {
        var sample = new[]
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
        SetStatus("Исправляю текст (qwen3.5:9b): 220 из 526…", StatusKind.Progress);
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
            if (doc.RootElement.TryGetProperty("language", out var lang))
                _language = Languages.Parse(lang.GetString());
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
                // Высота всегда задана явно, но NaN в JSON не сериализуется — подстрахуемся.
                glossaryHeight = double.IsNaN(GlossaryBox.Height) ? GlossaryMinHeight : GlossaryBox.Height,
                ownerName = OwnerNameBox.Text,
                diarizeSpeakers = DiarizeCheck.IsChecked == true,
                transcriptSettingsOpen = TranscriptSettingsToggle.IsChecked == true,
                language = _language.Language.ToString()
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
            SetStatus("Выберите модель Ollama.", StatusKind.Error);
            return;
        }

        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        var ct = _summaryCts.Token;

        MakeSummaryBtn.IsEnabled = false;
        RightTabs.SelectedIndex = 1;
        SummaryBox.Clear();
        SetStatus($"Формирую итоги ({model})…", StatusKind.Progress);
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
                SetStatus($"Итоги оборвались: модель «{model}» упёрлась в окно контекста или в предел длины ответа. " +
                          $"Сохранено: {Path.GetFileName(SummaryPath(_lastFile))}", StatusKind.Error);
            else
                SetStatus($"Итоги сохранены: {Path.GetFileName(SummaryPath(_lastFile))}", StatusKind.Done);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Формирование итогов отменено.");
        }
        catch (Exception ex) when (ex is OllamaUnavailableException or OllamaModelMissingException)
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка итогов: {ex.Message}", StatusKind.Error);
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
        Hint.Text = recording ? "идёт запись — нажмите, чтобы остановить" : "нажмите, чтобы начать запись";
        SystemSource.IsEnabled = CaptureDevices.IsEnabled = SettingsPanel.IsEnabled = !recording;
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
            SetStatus($"Ошибка записи: {err.Message}", StatusKind.Error);
            _ = StopRecordingAsync();
            return;
        }
        if (_stt?.Error is { } sttErr)
        {
            SetStatus($"Транскрипция остановлена: {sttErr.Message}", StatusKind.Error);
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
        public override string ToString() => Window?.ToString() ?? "Всё устройство вывода";
    }
}
