using System;
using System.Text;
using CallAudioRecorder.Services;

namespace CallAudioRecorder;

/// <summary>
/// Самопроверка диаризации: два WAV с разными голосами. Каждый режется пополам,
/// половины подаются вперемешку (w1a, w2a, w1b, w2b) — ожидается, что обе половины
/// первого файла получат «Собеседник 1», второго — «Собеседник 2».
/// </summary>
public static class DiarSpike
{
    public static void Run(string logPath, string wav1Path, string wav2Path)
    {
        var log = new StringBuilder();
        try
        {
            using var diarizer = new SpeakerDiarizer(TranscriptionService.SpeakerModelPath);

            var w1 = SttSpike.LoadMono16k(wav1Path);
            var w2 = SttSpike.LoadMono16k(wav2Path);
            var segments = new (string Wav, float[] Samples)[]
            {
                ("wav1a", w1[..(w1.Length / 2)]),
                ("wav2a", w2[..(w2.Length / 2)]),
                ("wav1b", w1[(w1.Length / 2)..]),
                ("wav2b", w2[(w2.Length / 2)..]),
            };
            var expected = new[] { "Собеседник 1", "Собеседник 2", "Собеседник 1", "Собеседник 2" };

            bool ok = true;
            for (int i = 0; i < segments.Length; i++)
            {
                var name = diarizer.Identify(segments[i].Samples);
                if (name != expected[i]) ok = false;
                log.AppendLine($"{segments[i].Wav} ({segments[i].Samples.Length / 16000.0:F1}s) " +
                               $"→ {name} (ожидалось {expected[i]}, score={diarizer.LastBestScore:F3})");
            }
            log.AppendLine($"speakers={diarizer.SpeakerCount}");
            log.Insert(0, ok ? "OK\n" : "FAIL\n");
        }
        catch (Exception ex)
        {
            log.AppendLine($"FAIL\n{ex}");
        }
        System.IO.File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
    }
}
