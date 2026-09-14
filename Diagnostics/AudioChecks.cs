using System;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;

namespace CallAudioRecorder.Diagnostics;

/// <summary>Аудиотракт: запись с устройств по умолчанию и захват звука одного приложения.</summary>
internal static class AudioChecks
{
    /// <summary>Запись N секунд без окна: системный звук + микрофон → MP3.</summary>
    public static void SelfTest(string logPath, int seconds)
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

    /// <summary>
    /// Захват звука одного приложения: сперва прямой замер уровня с ProcessLoopbackCapture,
    /// затем сквозная запись через RecordingEngine (микс с микрофоном → MP3).
    /// PID = 0 — просто список окон-кандидатов.
    /// </summary>
    public static void ProcessLoopback(string logPath, int processId, int seconds, string mode = "all")
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
}
