using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using NAudio.CoreAudioApi;

namespace CallAudioRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Скрытый режим самопроверки: запись N секунд без окна, отчёт в лог-файл.
        // Использование: CallAudioRecorder.exe --selftest <лог-файл> [секунды]
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            int seconds = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var s) ? s : 3;
            RunSelfTest(e.Args[1], seconds);
            Shutdown();
            return;
        }

        // Спайк: замер скорости распознавания (GigaAM v3 или Parakeet TDT).
        // Использование: CallAudioRecorder.exe --stt-test <лог-файл> <wav-файл> [ru|en]
        if (e.Args.Length >= 3 && e.Args[0] == "--stt-test")
        {
            var sttLang = e.Args.Length >= 4 && e.Args[3].StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? Services.Languages.English
                : Services.Languages.Russian;
            SttSpike.Run(e.Args[1], e.Args[2], sttLang);
            Shutdown();
            return;
        }

        // Самотест диаризации: два WAV с разными голосами.
        // Использование: CallAudioRecorder.exe --diar-test <лог-файл> <wav1> <wav2>
        if (e.Args.Length >= 4 && e.Args[0] == "--diar-test")
        {
            DiarSpike.Run(e.Args[1], e.Args[2], e.Args[3]);
            Shutdown();
            return;
        }

        // Сквозной самотест транскрипции: запись N секунд с живыми tap'ами.
        // Использование: CallAudioRecorder.exe --stt-selftest <лог-файл> [секунды]
        if (e.Args.Length >= 2 && e.Args[0] == "--stt-selftest")
        {
            int seconds = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var s2) ? s2 : 15;
            RunSttSelfTest(e.Args[1], seconds, LanguageArg(e.Args, 3));
            Shutdown();
            return;
        }

        // Тест итогов: прогоняет тестовый транскрипт через Ollama.
        // Использование: CallAudioRecorder.exe --summary-test <лог-файл> <модель>
        // Тест LLM-коррекции: транскрипт с искажёнными английскими терминами.
        // Использование: CallAudioRecorder.exe --fix-test <лог-файл> <модель>
        if (e.Args.Length >= 3 && e.Args[0] == "--fix-test")
        {
            var fixLang = LanguageArg(e.Args, 3);
            System.Threading.Tasks.Task.Run(() => RunFixTest(e.Args[1], e.Args[2], fixLang))
                .GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        if (e.Args.Length >= 3 && e.Args[0] == "--summary-test")
        {
            // Task.Run — чтобы await не пытался вернуться в заблокированный Dispatcher
            System.Threading.Tasks.Task.Run(() => RunSummaryTest(e.Args[1], e.Args[2]))
                .GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // Снимок окна + проверка вёрстки на наложения блоков.
        // Использование: CallAudioRecorder.exe --ui-shot <png-файл> <лог-файл>
        if (e.Args.Length >= 3 && e.Args[0] == "--ui-shot")
        {
            UiShot.Run(e.Args[1], e.Args[2]);
            Shutdown();
            return;
        }

        // Захват звука отдельного приложения. Без PID (или с 0) печатает список окон-кандидатов.
        // Использование: CallAudioRecorder.exe --proc-loopback-test <лог-файл> [PID] [секунды]
        if (e.Args.Length >= 2 && e.Args[0] == "--proc-loopback-test")
        {
            int pid = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var p2) ? p2 : 0;
            int secs = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var s3) ? s3 : 5;
            string mode = e.Args.Length >= 5 ? e.Args[4] : "all";
            RunProcessLoopbackTest(e.Args[1], pid, secs, mode);
            Shutdown();
            return;
        }

        // Итоги по транскрипту длинной встречи: проверка, что окно контекста не рвёт ответ.
        // Использование: CallAudioRecorder.exe --summary-long-test <лог-файл> <модель> [минуты]
        if (e.Args.Length >= 3 && e.Args[0] == "--summary-long-test")
        {
            int minutes = e.Args.Length >= 4 && int.TryParse(e.Args[3], out var m) ? m : 60;
            var longLang = LanguageArg(e.Args, 4);
            System.Threading.Tasks.Task.Run(() => RunSummaryLongTest(e.Args[1], e.Args[2], minutes, longLang))
                .GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // Порядок реплик в живой ленте: вставка вперемешку должна давать тот же транскрипт,
        // что и сортировка по времени после записи.
        // Использование: CallAudioRecorder.exe --order-test <лог-файл>
        if (e.Args.Length >= 2 && e.Args[0] == "--order-test")
        {
            RunOrderTest(e.Args[1]);
            Shutdown();
            return;
        }

        // Имена участников по сохранённому транскрипту: какая метка чьим именем становится.
        // Использование: CallAudioRecorder.exe --names-test <лог-файл> <файл .транскрипт.md> <модель>
        if (e.Args.Length >= 4 && e.Args[0] == "--names-test")
        {
            var namesLang = LanguageArg(e.Args, 4);
            System.Threading.Tasks.Task.Run(() => RunNamesTest(e.Args[1], e.Args[2], e.Args[3], namesLang))
                .GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        // Диаризация готовой записи: сколько спикеров до и после консолидации профилей.
        // Использование: CallAudioRecorder.exe --diar-file <лог-файл> <mp3 или wav>
        if (e.Args.Length >= 3 && e.Args[0] == "--diar-file")
        {
            float? merge = e.Args.Length >= 4 && float.TryParse(e.Args[3],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mt)
                ? mt : null;
            float? weak = e.Args.Length >= 5 && float.TryParse(e.Args[4],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wt)
                ? wt : null;
            DiarSpike.RunFile(e.Args[1], e.Args[2], merge, weak);
            Shutdown();
            return;
        }

        // Канонизация терминов по глоссарию на готовых транскриптах: показывает все замены,
        // чтобы ловить ложные срабатывания до того, как они попадут в живую запись.
        // Использование: CallAudioRecorder.exe --canon-test <лог-файл> <файл .md или папка>
        if (e.Args.Length >= 3 && e.Args[0] == "--canon-test")
        {
            RunCanonTest(e.Args[1], e.Args[2]);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// Просит модель сопоставить метки спикеров с именами из разговора и печатает,
    /// что она предложила и сколько реплик это затронуло.
    /// </summary>
    private static async System.Threading.Tasks.Task RunNamesTest(
        string logPath, string transcriptPath, string model, Services.LanguageProfile language)
    {
        try
        {
            var entries = LoadTranscript(transcriptPath);
            var labels = entries.Select(x => x.Speaker).Distinct().OrderBy(x => x).ToList();

            using var ollama = new Services.OllamaClient();
            var trace = new List<string>();
            var names = await Services.SpeakerNamer.DetectAsync(ollama, model, entries, language,
                trace: trace, ownerName: Setting("ownerName"));

            var log = new System.Text.StringBuilder();
            log.AppendLine(names.Count > 0 ? "OK" : "FAIL");
            log.AppendLine($"файл: {transcriptPath}");
            log.AppendLine($"реплик: {entries.Count}, меток: {labels.Count}, распознано имён: {names.Count}");
            log.AppendLine();
            foreach (var label in labels)
            {
                int count = entries.Count(x => x.Speaker == label);
                log.AppendLine(names.TryGetValue(label, out var name)
                    ? $"{label} ({count} реплик) → {name}"
                    : $"{label} ({count} реплик) → —");
            }
            log.AppendLine();
            log.AppendLine("--- РАЗБОР ---");
            foreach (var t in trace) log.AppendLine(t);

            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL{Environment.NewLine}{ex}{Environment.NewLine}");
        }
    }

    /// <summary>
    /// Проверяет <see cref="Services.TranscriptionService.AppendRecognized"/>: реплики подаются
    /// в порядке распознавания (перемешанном), а лента должна получиться такой же, как если бы
    /// их отсортировали по времени и склеили после записи.
    /// </summary>
    private static void RunOrderTest(string logPath)
    {
        try
        {
            var log = new System.Text.StringBuilder();
            var rnd = new Random(20260908);
            int failures = 0;

            for (int round = 0; round < 200; round++)
            {
                // Реплики двух каналов вперемешку, с паузами меньше и больше порога склейки.
                var spoken = new List<Models.TranscriptEntry>();
                var time = TimeSpan.Zero;
                for (int i = 0; i < 24; i++)
                {
                    var speaker = rnd.Next(3) switch { 0 => "Я", 1 => "Собеседник 1", _ => "Собеседник 2" };
                    var duration = TimeSpan.FromSeconds(rnd.Next(1, 12));
                    spoken.Add(new Models.TranscriptEntry(speaker, $"реплика {i}", time, duration));
                    time += duration + TimeSpan.FromSeconds(rnd.Next(0, 6));
                }

                // Порядок распознавания: длинный сегмент попадает в очередь позже короткого,
                // начавшегося после него, — перемешиваем, но недалеко от исходного места.
                var recognized = spoken
                    .Select((entry, i) => (entry, key: i + rnd.Next(0, 5)))
                    .OrderBy(x => x.key)
                    .Select(x => x.entry)
                    .ToList();

                var flat = new List<Models.TranscriptEntry>();
                var live = new List<Models.TranscriptEntry>();
                foreach (var entry in recognized)
                    live = Services.TranscriptionService.AppendRecognized(flat, entry);

                var expected = Services.TranscriptionService.MergeAdjacent(
                    spoken.OrderBy(x => x.StartTime));

                bool ordered = live.Zip(live.Skip(1)).All(p => p.First.StartTime <= p.Second.StartTime);
                bool same = live.Count == expected.Count
                            && live.Zip(expected).All(p => p.First.Speaker == p.Second.Speaker
                                                        && p.First.Text == p.Second.Text
                                                        && p.First.StartTime == p.Second.StartTime);
                if (ordered && same) continue;

                failures++;
                if (failures <= 3)
                {
                    log.AppendLine($"--- РАСХОЖДЕНИЕ (прогон {round}, порядок по времени: {ordered}) ---");
                    log.AppendLine($"лента:   {string.Join(" | ", live.Select(x => $"{x.TimeLabel} {x.Speaker}: {x.Text}"))}");
                    log.AppendLine($"эталон:  {string.Join(" | ", expected.Select(x => $"{x.TimeLabel} {x.Speaker}: {x.Text}"))}");
                }
            }

            log.Insert(0, failures == 0
                ? $"OK{Environment.NewLine}200 прогонов: лента совпала с транскриптом после сортировки{Environment.NewLine}"
                : $"FAIL{Environment.NewLine}расхождений: {failures} из 200{Environment.NewLine}");
            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL{Environment.NewLine}{ex}{Environment.NewLine}");
        }
    }

    /// <summary>Значение поля из settings.json — чтобы самотесты работали с теми же настройками, что и UI.</summary>
    private static string Setting(string name)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallAudioRecorder", "settings.json");
        if (!File.Exists(path)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Читает сохранённый `*.транскрипт.md` обратно в реплики.</summary>
    private static List<Models.TranscriptEntry> LoadTranscript(string path)
    {
        var line = new System.Text.RegularExpressions.Regex(
            @"^\*\*\[(\d\d):(\d\d):(\d\d)\]\s*(.+?):\*\*\s*(.*)$");
        var result = new List<Models.TranscriptEntry>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var m = line.Match(raw.Trim());
            if (!m.Success) continue;
            var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value));
            result.Add(new Models.TranscriptEntry(m.Groups[4].Value, m.Groups[5].Value, start));
        }
        return result;
    }

    /// <summary>
    /// Прогоняет <see cref="Services.TermCanonizer"/> по сохранённым транскриптам и пишет
    /// в лог все замены с частотой. Глоссарий берётся из settings.json — то есть проверяется
    /// ровно тот список, с которым работает приложение.
    /// </summary>
    private static void RunCanonTest(string logPath, string source)
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CallAudioRecorder", "settings.json");
            string glossary = "";
            if (File.Exists(settingsPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("glossary", out var g)) glossary = g.GetString() ?? "";
            }

            var canonizer = Services.TermCanonizer.Build(glossary);
            if (canonizer is null)
            {
                File.WriteAllText(logPath, "FAIL\nВ глоссарии нет латинских терминов — канонизировать нечего.\n",
                    System.Text.Encoding.UTF8);
                return;
            }

            var files = Directory.Exists(source)
                ? Directory.GetFiles(source, "*.транскрипт.md")
                : new[] { source };

            var replacements = new List<(string From, string To)>();
            var versions = new List<(string From, string To)>();
            int lines = 0, changed = 0;

            foreach (var file in files)
            {
                var entries = LoadTranscript(file);
                lines += entries.Count;
                foreach (var entry in entries)
                {
                    int before = replacements.Count;
                    canonizer.Apply(entry.Text, replacements);
                    if (replacements.Count > before) changed++;
                }
                // Версии считаются по записи целиком: семейство «1.8.x» подтверждается
                // в одном месте, а применяется ко всем «183» файла.
                Services.VersionNormalizer.Normalize(entries, versions);
            }

            var log = new System.Text.StringBuilder();
            log.AppendLine(replacements.Count > 0 ? "OK" : "FAIL");
            log.AppendLine($"файлов: {files.Length}, реплик: {lines}, с заменами: {changed}, замен: {replacements.Count}");
            log.AppendLine();
            log.AppendLine("--- ЗАМЕНЫ (частота, было → стало) ---");
            foreach (var group in replacements.GroupBy(r => $"{r.From} → {r.To}").OrderByDescending(g => g.Count()))
                log.AppendLine($"{group.Count(),4}  {group.Key}");

            log.AppendLine();
            log.AppendLine($"--- ВЕРСИИ (замен: {versions.Count}) ---");
            foreach (var group in versions.GroupBy(r => $"{r.From} → {r.To}").OrderByDescending(g => g.Count()))
                log.AppendLine($"{group.Count(),4}  {group.Key}");

            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>Язык из аргументов командной строки: «en»/«english» → английский, иначе русский.</summary>
    private static Services.LanguageProfile LanguageArg(string[] args, int index) =>
        args.Length > index && args[index].StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? Services.Languages.English
            : Services.Languages.Russian;

    private static async System.Threading.Tasks.Task RunFixTest(string logPath, string model,
        Services.LanguageProfile? language = null)
    {
        try
        {
            var profile = language ?? Services.Languages.Russian;
            bool english = profile.Language == Services.TranscriptionLanguage.English;

            // Реплики в том виде, в каком их обычно портит ASR соответствующего языка
            var raw = english
                ? new List<Models.TranscriptEntry>
                {
                    new("Me", "we need to deploy the feature to prod via github actions before the dead line",
                        TimeSpan.FromSeconds(5)),
                    new("Speaker", "i will review the pull request and spin up the kubernetes cluster",
                        TimeSpan.FromSeconds(15)),
                    new("Me", "the api returns a five hundred when the backend does not respond",
                        TimeSpan.FromSeconds(25)),
                    new("Speaker", "lets discuss it at the next sprint review", TimeSpan.FromSeconds(35)),
                }
                : new List<Models.TranscriptEntry>
                {
                    new("Я", "надо задеплоить фичу на прод через гитхаб экшенс до дед лайна", TimeSpan.FromSeconds(5)),
                    new("Собеседник", "я посмотрю пул реквест и подниму кубернетес кластер", TimeSpan.FromSeconds(15)),
                    new("Я", "эйпиай отдаёт пятисотку когда бэкенд не отвечает", TimeSpan.FromSeconds(25)),
                    new("Собеседник", "давай обсудим на следующем спринт ревью", TimeSpan.FromSeconds(35)),
                };

            using var ollama = new Services.OllamaClient();
            var fixedEntries = await Services.TranscriptCorrector.CorrectAsync(
                ollama, model, raw, "GitHub Actions, Kubernetes, API, deadline, pull request, sprint review",
                progress: null, language: profile);

            var sb = new System.Text.StringBuilder("OK\n--- БЫЛО ---\n");
            foreach (var en in raw) sb.AppendLine(en.ToString());
            sb.AppendLine("--- СТАЛО ---");
            foreach (var en in fixedEntries) sb.AppendLine(en.ToString());
            File.WriteAllText(logPath, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    private static async System.Threading.Tasks.Task RunSummaryTest(string logPath, string model)
    {
        try
        {
            // Названия в репликах намеренно искажены так, как их ломает распознавание
            // («Firebace», «Крейзи Геймс»): проверяем, что глоссарий доходит до итогов
            // и модель пишет канонические формы, а не разнобой из транскрипта.
            var transcript = new List<Models.TranscriptEntry>
            {
                new("Я", "Коллеги, предлагаю перенести релиз на пятницу, нужно закрыть баг с оплатой.", TimeSpan.FromSeconds(5)),
                new("Собеседник", "Согласен. Тогда я подготовлю тестовый стенд к четвергу и проверю Firebace.", TimeSpan.FromSeconds(15)),
                new("Я", "Хорошо, а я до среды допишу интеграцию с платёжным шлюзом и выложу билд на Крейзи Геймс.", TimeSpan.FromSeconds(25)),
                new("Собеседник", "Остаётся открытым вопрос по дизайну главного экрана — обсудим на следующей встрече.", TimeSpan.FromSeconds(40)),
            };
            const string glossary = "Firebase, Crazy Games";

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage: null, outcome, glossary: glossary))
            {
                sb.Append(chunk);
            }

            var answer = sb.ToString();
            var terms = new[] { "Firebase", "Crazy Games" };
            var restored = terms.Where(t => answer.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            var status = restored.Count == terms.Length ? "OK" : "FAIL";
            File.WriteAllText(logPath,
                $"{status}\ndone_reason={outcome.DoneReason}\n" +
                $"глоссарий: восстановлено {restored.Count} из {terms.Length} ({string.Join(", ", restored)})\n{answer}",
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>
    /// Захват звука одного приложения: сперва прямой замер уровня с ProcessLoopbackCapture,
    /// затем сквозная запись через RecordingEngine (микс с микрофоном → MP3).
    /// PID = 0 — просто список окон-кандидатов.
    /// </summary>
    private static void RunProcessLoopbackTest(string logPath, int processId, int seconds, string mode = "all")
    {
        try
        {
            var log = new System.Text.StringBuilder();
            if (processId == 0)
            {
                var windows = Services.AudioWindows.Enumerate();
                log.AppendLine("OK").AppendLine($"окон: {windows.Count}");
                foreach (var w in windows)
                    log.AppendLine($"PID={w.ProcessId,-7} {(w.PlayingAudio ? "♪" : " ")} {w.ProcessName,-24} {w.Title}");
                File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
                return;
            }

            long bytes = 0;
            float peak = 0;
            double sumSquares = 0;
            long samples = 0;

            if (mode != "engine")
            using (var capture = new Services.ProcessLoopbackCapture(processId))
            {
                capture.DataAvailable += (_, e) =>
                {
                    bytes += e.BytesRecorded;
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                    {
                        float v = BitConverter.ToInt16(e.Buffer, i) / 32768f;
                        sumSquares += (double)v * v;
                        samples++;
                        if (Math.Abs(v) > peak) peak = Math.Abs(v);
                    }
                };
                capture.StartRecording();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                capture.StopRecording();
            }

            double rms = samples > 0 ? Math.Sqrt(sumSquares / samples) : 0;
            log.AppendLine($"прямой захват: байт={bytes} пик={peak:F4} rms={rms:F4}");

            if (mode == "direct")
            {
                File.WriteAllText(logPath, (bytes > 0 ? "OK\n" : "FAIL\n") + log, System.Text.Encoding.UTF8);
                return;
            }

            // Сквозная проверка: тот же источник, но уже внутри конвейера записи.
            using var enumerator = new MMDeviceEnumerator();
            using var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var mp3Path = Path.Combine(Path.GetTempPath(), $"procloop_{DateTime.Now:HHmmss}.mp3");
            using (var engine = new RecordingEngine(null, mic, mp3Path, 192, false, processId))
            {
                engine.Start();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                engine.Stop();
                if (engine.Error is { } err) throw err;
            }
            long mp3Bytes = new FileInfo(mp3Path).Length;
            File.Delete(mp3Path);
            log.AppendLine($"через RecordingEngine: mp3bytes={mp3Bytes}");

            bool ok = (mode == "engine" || bytes > 0) && mp3Bytes > 1024;
            File.WriteAllText(logPath, (ok ? "OK\n" : "FAIL\n") + log, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>
    /// Итоги по транскрипту длинной встречи. Час разговора — это ~40 000 токенов, которые
    /// в окно локальной модели не влезают: раньше Ollama урезал промпт и ответ обрывался
    /// на нескольких строках. Проверяем, что все пять разделов итогов на месте.
    /// </summary>
    private static async System.Threading.Tasks.Task RunSummaryLongTest(string logPath, string model, int minutes,
        Services.LanguageProfile? language = null)
    {
        try
        {
            var profile = language ?? Services.Languages.Russian;
            var transcript = SyntheticTranscript(minutes, profile);
            var text = string.Join("\n", transcript);

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            var stages = new List<string>();
            var stage = new Progress<string>(stages.Add);

            var started = DateTime.Now;
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage, outcome, profile))
            {
                sb.Append(chunk);
            }
            var answer = sb.ToString();

            var sections = Services.SummaryPrompt.SectionsFor(profile);
            var missing = new List<string>();
            foreach (var section in sections)
                if (!answer.Contains(section, StringComparison.Ordinal)) missing.Add(section);

            bool ok = missing.Count == 0 && !outcome.HitContextLimit;
            var log = new System.Text.StringBuilder(ok ? "OK\n" : "FAIL\n");
            log.AppendLine($"язык={profile.DisplayName} минут={minutes} реплик={transcript.Count} " +
                           $"токенов_на_входе≈{Services.OllamaClient.EstimateTokens(text)}");
            log.AppendLine($"done_reason={outcome.DoneReason} длина_ответа={answer.Length} " +
                           $"время={(DateTime.Now - started).TotalSeconds:F0}с");
            log.AppendLine($"нет_разделов: {(missing.Count == 0 ? "—" : string.Join(", ", missing))}");
            if (stages.Count > 0) log.AppendLine($"этапы: {string.Join(" | ", stages)}");
            log.AppendLine("--- ИТОГИ ---").AppendLine(answer);
            File.WriteAllText(logPath, log.ToString(), System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    /// <summary>Синтетический транскрипт заданной длительности: реплика раз в ~8 секунд.</summary>
    private static List<Models.TranscriptEntry> SyntheticTranscript(int minutes, Services.LanguageProfile profile)
    {
        bool english = profile.Language == Services.TranscriptionLanguage.English;

        string[] mineEn =
        [
            "Let's start with the status of the payment gateway integration.",
            "I closed the authorisation task yesterday, the tests are still pending.",
            "I suggest moving this logic into a separate service, it will be easier to maintain.",
            "We are on track for Friday unless something else comes up.",
            "Let's agree the report goes out before the end of the month.",
            "There was a bug with timeouts, I fixed it but it needs a review.",
        ];
        string[] theirsEn =
        [
            "Agreed, but we need to decide who owns the database migration.",
            "I will look at the load and come back with numbers tomorrow.",
            "The client asked for a spreadsheet export, that is a big piece of work.",
            "I will bring the staging environment up by Thursday, it is missing access rights.",
            "Let's move the design question to the next meeting.",
            "Production went down twice this week, we need to find the root cause.",
        ];

        string[] mine =
        [
            "Давайте начнём со статуса по интеграции с платёжным шлюзом.",
            "Я вчера закрыл задачу по авторизации, осталось прогнать тесты.",
            "Предлагаю вынести эту логику в отдельный сервис, поддерживать будет проще.",
            "По срокам мы успеваем к пятнице, если не всплывёт что-то ещё.",
            "Зафиксируем: отчёт готовим до конца месяца.",
            "Тут был баг с таймаутами, я его починил, нужно ревью.",
        ];
        string[] theirs =
        [
            "Согласен, но нужно понять, кто отвечает за миграцию базы.",
            "Я посмотрю на нагрузку и вернусь с цифрами завтра.",
            "Клиент просил добавить выгрузку в таблицу, это большая работа.",
            "Тестовый стенд подниму к четвергу, там не хватает доступов.",
            "Давайте вынесем вопрос по дизайну на следующую встречу.",
            "У нас упало на проде дважды за неделю, надо разобраться с причиной.",
        ];

        var entries = new List<Models.TranscriptEntry>();
        var random = new Random(42);
        for (int i = 0, count = minutes * 60 / 8; i < count; i++)
        {
            var speaker = i % 3 == 0 ? profile.MeLabel
                        : i % 3 == 1 ? profile.NumberedOther(1)
                                     : profile.NumberedOther(2);
            bool isMine = speaker == profile.MeLabel;
            var pool = english
                ? (isMine ? mineEn : theirsEn)
                : (isMine ? mine : theirs);
            entries.Add(new Models.TranscriptEntry(
                speaker, pool[random.Next(pool.Length)],
                TimeSpan.FromSeconds(i * 8), TimeSpan.FromSeconds(7)));
        }
        return entries;
    }

    private static void RunSttSelfTest(string logPath, int seconds, Services.LanguageProfile? language = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var mp3Path = Path.Combine(Path.GetTempPath(), $"sttselftest_{DateTime.Now:HHmmss}.mp3");
            System.Collections.Generic.IReadOnlyList<Models.TranscriptEntry> entries;
            using (var engine = new RecordingEngine(render, capture, mp3Path, 192, enableTranscriptionTaps: true))
            using (var stt = new Services.TranscriptionService(language, diarizeSpeakers: true))
            {
                stt.Start(engine.MicTap16k!, engine.SystemTap16k!, () => engine.Elapsed);
                engine.Start();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                engine.Stop();
                entries = stt.StopAndDrain();
                if (engine.Error is { } err) throw err;
                if (stt.Error is { } sttErr) throw sttErr;
            }

            var sb = new System.Text.StringBuilder("OK\n");
            sb.AppendLine($"mp3bytes={new FileInfo(mp3Path).Length} entries={entries.Count}");
            foreach (var en in entries) sb.AppendLine(en.ToString());
            File.WriteAllText(logPath, sb.ToString());
            File.Delete(mp3Path);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }

    private static void RunSelfTest(string logPath, int seconds)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var mp3Path = Path.Combine(Path.GetTempPath(), $"selftest_{DateTime.Now:HHmmss}.mp3");
            using (var engine = new RecordingEngine(render, capture, mp3Path, 192))
            {
                engine.Start();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                engine.Stop();
                if (engine.Error is { } err) throw err;
            }

            var size = new FileInfo(mp3Path).Length;
            File.WriteAllText(logPath,
                $"OK\nrender={render.FriendlyName}\ncapture={capture.FriendlyName}\nfile={mp3Path}\nbytes={size}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
    }
}
