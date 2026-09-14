using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CallAudioRecorder.Models;
using NAudio.CoreAudioApi;

namespace CallAudioRecorder.Diagnostics;

/// <summary>Распознавание: сквозная запись с живыми tap'ами.</summary>
internal static class SttChecks
{
    /// <summary>Запись N секунд с живой транскрипцией и диаризацией: реплики — в лог.</summary>
    public static void LiveTranscription(string logPath, int seconds, Services.LanguageProfile? language = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var capture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            var mp3Path = Path.Combine(Path.GetTempPath(), $"sttselftest_{DateTime.Now:HHmmss}.mp3");
            IReadOnlyList<TranscriptEntry> entries;
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
}
