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
            await foreach (var chunk in ollama.ChatStreamAsync(
                model, Services.SummaryPrompt.System, Services.SummaryPrompt.BuildUserMessage(transcript)))
            {
                sb.Append(chunk);
            }
            File.WriteAllText(logPath, "OK\n" + sb, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(logPath, $"FAIL\n{ex}\n");
        }
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
