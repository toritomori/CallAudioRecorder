using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CallAudioRecorder.Services;
using SherpaOnnx;

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

    /// <summary>
    /// Диаризация готовой записи: VAD режет файл на реплики, каждая уходит в диаризатор,
    /// затем профили пересобираются. Печатает, сколько «собеседников» получилось до и после —
    /// на живых записях именно здесь видно, сколько из них были фантомами.
    /// </summary>
    public static void RunFile(string logPath, string audioPath, float? merge = null, float? weak = null)
    {
        var log = new StringBuilder();
        try
        {
            if (merge is not null) SpeakerDiarizer.MergeThreshold = merge.Value;
            if (weak is not null) SpeakerDiarizer.WeakMergeThreshold = weak.Value;
            log.AppendLine($"пороги слияния: обычный {SpeakerDiarizer.MergeThreshold:F2}, " +
                           $"для осколков {SpeakerDiarizer.WeakMergeThreshold:F2}");
            // MP3 читается через Media Foundation, а её надо поднять руками:
            // в WAV-режимах (--stt-test) декодер не нужен, поэтому раньше не всплывало.
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            var samples = SttSpike.LoadMono16k(audioPath);
            log.AppendLine($"файл: {audioPath}");
            log.AppendLine($"длительность: {samples.Length / 16000.0 / 60:F1} мин");

            using var diarizer = new SpeakerDiarizer(TranscriptionService.SpeakerModelPath);

            var vadConfig = new VadModelConfig();
            vadConfig.SileroVad.Model = System.IO.Path.Combine(TranscriptionService.ModelsRoot, "silero_vad.onnx");
            vadConfig.SileroVad.Threshold = 0.5f;
            vadConfig.SileroVad.MinSilenceDuration = 0.5f;
            vadConfig.SileroVad.MinSpeechDuration = 0.25f;
            vadConfig.SileroVad.MaxSpeechDuration = 15f;
            vadConfig.SileroVad.WindowSize = 512;
            vadConfig.SampleRate = 16000;
            using var vad = new VoiceActivityDetector(vadConfig, 60);

            var assigned = new List<string>();
            const int window = 512;
            for (int offset = 0; offset < samples.Length; offset += window)
            {
                int count = Math.Min(window, samples.Length - offset);
                vad.AcceptWaveform(samples.AsSpan(offset, count).ToArray());
                Drain(vad, diarizer, assigned);
            }
            vad.Flush();
            Drain(vad, diarizer, assigned);

            var before = Counts(assigned);
            log.AppendLine();
            log.AppendLine($"--- ДО КОНСОЛИДАЦИИ: сегментов {assigned.Count}, спикеров {before.Count} ---");
            log.AppendLine(Describe(before));

            log.AppendLine();
            log.AppendLine("--- БЛИЗОСТЬ ЦЕНТРОИДОВ (топ-25) ---");
            foreach (var d in diarizer.ProfileDistances().OrderByDescending(d => d.Score).Take(25))
                log.AppendLine($"{d.Score:F3}  {d.A} ({d.WeightA}) ↔ {d.B} ({d.WeightB})");

            var renames = diarizer.Consolidate();
            var after = Counts(assigned.Select(a => renames.TryGetValue(a, out var n) ? n : a).ToList());
            log.AppendLine();
            log.AppendLine($"--- ПОСЛЕ: спикеров {after.Count} ---");
            log.AppendLine(Describe(after));

            if (renames.Count > 0)
            {
                log.AppendLine();
                log.AppendLine("--- СЛИЯНИЯ ---");
                foreach (var (from, to) in renames.OrderBy(r => r.Key.Length).ThenBy(r => r.Key))
                    log.AppendLine($"{from} → {to}");
            }

            // Успех — если фантомов (профилей на одной-двух репликах) стало меньше.
            int weakBefore = before.Count(p => p.Value <= 2);
            int weakAfter = after.Count(p => p.Value <= 2);
            log.AppendLine();
            log.AppendLine($"профилей на ≤2 репликах: было {weakBefore}, стало {weakAfter}");
            log.Insert(0, (weakAfter <= weakBefore ? "OK" : "FAIL") + Environment.NewLine);
        }
        catch (Exception ex)
        {
            log.Insert(0, $"FAIL{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        System.IO.File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
    }

    private static void Drain(VoiceActivityDetector vad, SpeakerDiarizer diarizer, List<string> assigned)
    {
        while (!vad.IsEmpty())
        {
            var seg = vad.Front();
            vad.Pop();
            assigned.Add(diarizer.Identify(seg.Samples));
        }
    }

    private static Dictionary<string, int> Counts(IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, int>();
        foreach (var n in names) result[n] = result.GetValueOrDefault(n) + 1;
        return result;
    }

    private static string Describe(Dictionary<string, int> counts) =>
        string.Join(", ", counts.OrderByDescending(p => p.Value).Select(p => $"{p.Key}: {p.Value}"));
}
