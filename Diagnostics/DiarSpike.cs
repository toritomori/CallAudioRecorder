// Пробник: в релизную сборку не попадает — его флаги регистрируются в DiagnosticModes только в Debug.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CallAudioRecorder.Services;
using SherpaOnnx;

namespace CallAudioRecorder.Diagnostics;

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
    /// <param name="padSeconds">
    /// На сколько расширять сегмент в обе стороны перед диаризацией. По умолчанию 0 — как
    /// в приложении: паддинг нужен распознавателю, а диаризатору идёт ядро VAD-сегмента.
    /// </param>
    public static void RunFile(string logPath, string audioPath, float? merge = null, float? weak = null,
        float? padSeconds = null, float? newSpeaker = null, float? newSeconds = null)
    {
        var log = new StringBuilder();
        try
        {
            if (merge is not null) SpeakerDiarizer.MergeThreshold = merge.Value;
            if (weak is not null) SpeakerDiarizer.WeakMergeThreshold = weak.Value;
            if (newSpeaker is not null) SpeakerDiarizer.NewSpeakerThreshold = newSpeaker.Value;
            if (newSeconds is not null) SpeakerDiarizer.MinNewSpeakerSeconds = newSeconds.Value;
            double padding = padSeconds ?? 0;
            log.AppendLine($"пороги слияния: обычный {SpeakerDiarizer.MergeThreshold:F2}, " +
                           $"для осколков {SpeakerDiarizer.WeakMergeThreshold:F2}; паддинг {padding:F2} с");
            log.AppendLine($"новый спикер: близость ниже {SpeakerDiarizer.NewSpeakerThreshold:F2}, " +
                           $"сегмент от {SpeakerDiarizer.MinNewSpeakerSeconds:F1} с");
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

            // Отдельный extractor для половин сегмента: диаризатор свой держит внутри.
            using var halves = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
            {
                Model = TranscriptionService.SpeakerModelPath,
                NumThreads = 1,
                Provider = "cpu"
            });
            int pad = (int)(padding * 16000);

            var assigned = new List<string>();
            var decisions = new List<Decision>();
            const int window = 512;
            for (int offset = 0; offset < samples.Length; offset += window)
            {
                int count = Math.Min(window, samples.Length - offset);
                vad.AcceptWaveform(samples.AsSpan(offset, count).ToArray());
                Drain(vad, diarizer, assigned, decisions, samples, pad, halves);
            }
            vad.Flush();
            Drain(vad, diarizer, assigned, decisions, samples, pad, halves);

            var before = Counts(assigned);
            log.AppendLine();
            log.AppendLine($"--- ДО КОНСОЛИДАЦИИ: сегментов {assigned.Count}, спикеров {before.Count} ---");
            log.AppendLine(Describe(before));

            DescribeDecisions(log, decisions);
            DescribeMixedSegments(log, decisions);

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

    /// <summary>
    /// Живое решение по сегменту: с какой близостью к лучшему профилю он пришёл и завёл ли
    /// нового спикера. <paramref name="HalfSimilarity"/> — близость первой и второй половины
    /// сегмента (null для коротких): низкая значит, что внутри сменился голос.
    /// </summary>
    private sealed record Decision(double Start, double Seconds, string Speaker, float Score,
        bool Created, float? HalfSimilarity);

    /// <summary>С какой длины сегмент делится пополам для проверки на смену голоса внутри.</summary>
    private const double MinSplitSeconds = 4.0;

    private static void Drain(VoiceActivityDetector vad, SpeakerDiarizer diarizer, List<string> assigned,
        List<Decision> decisions, float[] audio, int pad, SpeakerEmbeddingExtractor halves)
    {
        while (!vad.IsEmpty())
        {
            var seg = vad.Front();
            vad.Pop();

            // Паддинг — только для сравнения с прежним поведением, когда диаризатор получал
            // сегмент, расширенный для распознавания.
            int start = (int)Math.Max(0, seg.Start - pad);
            int end = (int)Math.Min(audio.Length, seg.Start + seg.Samples.Length + pad);
            var samples = audio[start..end];

            int profilesBefore = diarizer.SpeakerCount;
            var name = diarizer.Identify(samples);
            assigned.Add(name);

            float? half = null;
            if (samples.Length >= MinSplitSeconds * 16000)
            {
                var a = Embed(halves, samples[..(samples.Length / 2)]);
                var b = Embed(halves, samples[(samples.Length / 2)..]);
                if (a is not null && b is not null) half = Dot(a, b);
            }

            decisions.Add(new Decision(start / 16000.0, samples.Length / 16000.0, name,
                profilesBefore == 0 ? 0 : diarizer.LastBestScore,
                diarizer.SpeakerCount > profilesBefore, half));
        }
    }

    private static float[]? Embed(SpeakerEmbeddingExtractor extractor, float[] samples)
    {
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(16000, samples);
        stream.InputFinished();
        if (!extractor.IsReady(stream)) return null;
        var v = extractor.Compute(stream);
        double norm = 0;
        foreach (var x in v) norm += x * x;
        float inv = norm > 0 ? (float)(1 / Math.Sqrt(norm)) : 0;
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)dot;
    }

    /// <summary>
    /// Где живое решение заводит фантомов: каждое создание профиля с близостью к лучшему
    /// из уже известных, и распределение близости у сегментов, которые прилипли к профилю.
    /// </summary>
    private static void DescribeDecisions(StringBuilder log, List<Decision> decisions)
    {
        log.AppendLine();
        log.AppendLine("--- СОЗДАНИЕ ПРОФИЛЕЙ (время, длина, близость к лучшему известному, близость половин) ---");
        foreach (var d in decisions.Where(d => d.Created))
            log.AppendLine($"{TimeSpan.FromSeconds(d.Start):hh\\:mm\\:ss}  {d.Seconds,5:F1}s  " +
                           $"{d.Score:F3}  половины {(d.HalfSimilarity is { } h ? h.ToString("F3") : "—")}  → {d.Speaker}");

        log.AppendLine();
        log.AppendLine("--- БЛИЗОСТЬ К ЛУЧШЕМУ ПРОФИЛЮ (сегменты ≥2 с, прилипшие / создавшие) ---");
        var enrolled = decisions.Where(d => d.Seconds >= 2 && d.Score > 0).ToList();
        for (float lo = 0.20f; lo < 1.0f; lo += 0.05f)
        {
            float hi = lo + 0.05f;
            int joined = enrolled.Count(d => !d.Created && d.Score >= lo && d.Score < hi);
            int created = enrolled.Count(d => d.Created && d.Score >= lo && d.Score < hi);
            if (joined + created > 0) log.AppendLine($"{lo:F2}–{hi:F2}: {joined,4} / {created}");
        }
        log.AppendLine($"сегментов короче 2 с: {decisions.Count(d => d.Seconds < 2)} из {decisions.Count}");
    }

    /// <summary>
    /// Сегменты, внутри которых сменился голос: системный канал — это микс всех собеседников,
    /// и при быстрой смене реплик без паузы в 0.5 с VAD отдаёт двоих одним куском.
    /// </summary>
    private static void DescribeMixedSegments(StringBuilder log, List<Decision> decisions)
    {
        var split = decisions.Where(d => d.HalfSimilarity is not null).ToList();
        log.AppendLine();
        log.AppendLine($"--- БЛИЗОСТЬ ПОЛОВИН СЕГМЕНТА (сегменты ≥{MinSplitSeconds:F0} с: всего / из них создали профиль) ---");
        for (float lo = -0.10f; lo < 1.0f; lo += 0.1f)
        {
            float hi = lo + 0.1f;
            var bin = split.Where(d => d.HalfSimilarity >= lo && d.HalfSimilarity < hi).ToList();
            if (bin.Count > 0) log.AppendLine($"{lo:F1}–{hi:F1}: {bin.Count,4} / {bin.Count(d => d.Created)}");
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
#endif
