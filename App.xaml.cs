using System;
using System.Collections.Generic;
using System.IO;
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

        // Спайк: замер скорости распознавания GigaAM v3.
        // Использование: CallAudioRecorder.exe --stt-test <лог-файл> <wav-файл>
        if (e.Args.Length >= 3 && e.Args[0] == "--stt-test")
        {
            SttSpike.Run(e.Args[1], e.Args[2]);
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
            RunSttSelfTest(e.Args[1], seconds);
            Shutdown();
            return;
        }

        // Тест итогов: прогоняет тестовый транскрипт через Ollama.
        // Использование: CallAudioRecorder.exe --summary-test <лог-файл> <модель>
        // Тест LLM-коррекции: транскрипт с искажёнными английскими терминами.
        // Использование: CallAudioRecorder.exe --fix-test <лог-файл> <модель>
        if (e.Args.Length >= 3 && e.Args[0] == "--fix-test")
        {
            System.Threading.Tasks.Task.Run(() => RunFixTest(e.Args[1], e.Args[2]))
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
            System.Threading.Tasks.Task.Run(() => RunSummaryLongTest(e.Args[1], e.Args[2], minutes))
                .GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private static async System.Threading.Tasks.Task RunFixTest(string logPath, string model)
    {
        try
        {
            // Реплики в том виде, в каком их обычно портит русскоязычный ASR
            var raw = new List<Models.TranscriptEntry>
            {
                new("Я", "надо задеплоить фичу на прод через гитхаб экшенс до дед лайна", TimeSpan.FromSeconds(5)),
                new("Собеседник", "я посмотрю пул реквест и подниму кубернетес кластер", TimeSpan.FromSeconds(15)),
                new("Я", "эйпиай отдаёт пятисотку когда бэкенд не отвечает", TimeSpan.FromSeconds(25)),
                new("Собеседник", "давай обсудим на следующем спринт ревью", TimeSpan.FromSeconds(35)),
            };

            using var ollama = new Services.OllamaClient();
            var fixedEntries = await Services.TranscriptCorrector.CorrectAsync(
                ollama, model, raw, "GitHub Actions, Kubernetes, API, deadline, pull request, sprint review");

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
            var transcript = new List<Models.TranscriptEntry>
            {
                new("Я", "Коллеги, предлагаю перенести релиз на пятницу, нужно закрыть баг с оплатой.", TimeSpan.FromSeconds(5)),
                new("Собеседник", "Согласен. Тогда я подготовлю тестовый стенд к четвергу.", TimeSpan.FromSeconds(15)),
                new("Я", "Хорошо, а я до среды допишу интеграцию с платёжным шлюзом.", TimeSpan.FromSeconds(25)),
                new("Собеседник", "Остаётся открытым вопрос по дизайну главного экрана — обсудим на следующей встрече.", TimeSpan.FromSeconds(40)),
            };

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage: null, outcome))
            {
                sb.Append(chunk);
            }
            File.WriteAllText(logPath, $"OK\ndone_reason={outcome.DoneReason}\n{sb}", System.Text.Encoding.UTF8);
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
    private static async System.Threading.Tasks.Task RunSummaryLongTest(string logPath, string model, int minutes)
    {
        try
        {
            var transcript = SyntheticTranscript(minutes);
            var text = string.Join("\n", transcript);

            using var ollama = new Services.OllamaClient();
            var sb = new System.Text.StringBuilder();
            var outcome = new Services.ChatOutcome();
            var stages = new List<string>();
            var stage = new Progress<string>(stages.Add);

            var started = DateTime.Now;
            await foreach (var chunk in Services.SummaryComposer.ComposeAsync(
                ollama, model, transcript, stage, outcome))
            {
                sb.Append(chunk);
            }
            var answer = sb.ToString();

            string[] sections =
            [
                "## Краткое резюме", "## Ключевые темы и решения",
                "## Задачи", "## Открытые вопросы", "## Следующие шаги"
            ];
            var missing = new List<string>();
            foreach (var section in sections)
                if (!answer.Contains(section, StringComparison.Ordinal)) missing.Add(section);

            bool ok = missing.Count == 0 && !outcome.HitContextLimit;
            var log = new System.Text.StringBuilder(ok ? "OK\n" : "FAIL\n");
            log.AppendLine($"минут={minutes} реплик={transcript.Count} " +
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
    private static List<Models.TranscriptEntry> SyntheticTranscript(int minutes)
    {
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
            var speaker = i % 3 == 0 ? "Я" : i % 3 == 1 ? "Собеседник 1" : "Собеседник 2";
            var pool = speaker == "Я" ? mine : theirs;
            entries.Add(new Models.TranscriptEntry(
                speaker, pool[random.Next(pool.Length)],
                TimeSpan.FromSeconds(i * 8), TimeSpan.FromSeconds(7)));
        }
        return entries;
    }

    private static void RunSttSelfTest(string logPath, int seconds)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var mp3Path = Path.Combine(Path.GetTempPath(), $"sttselftest_{DateTime.Now:HHmmss}.mp3");
            System.Collections.Generic.IReadOnlyList<Models.TranscriptEntry> entries;
            using (var engine = new RecordingEngine(render, capture, mp3Path, 192, enableTranscriptionTaps: true))
            using (var stt = new Services.TranscriptionService(diarizeSpeakers: true))
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
