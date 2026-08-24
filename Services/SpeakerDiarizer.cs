using System;
using System.Collections.Generic;
using SherpaOnnx;

namespace CallAudioRecorder.Services;

/// <summary>
/// Живое разделение собеседников по голосу: считает embedding голоса (CAM++)
/// для каждого речевого сегмента системного канала и сопоставляет его с профилями
/// уже слышанных спикеров по косинусной близости. Новые спикеры получают имена
/// «Собеседник 1», «Собеседник 2», …
///
/// НЕ потокобезопасен — вызывается только из потока SttWorker.
/// </summary>
public sealed class SpeakerDiarizer : IDisposable
{
    private const int Rate = 16000;

    /// <summary>
    /// Минимальная косинусная близость к профилю, чтобы считать голос знакомым.
    /// Калибровка по --diar-test: один голос ≈ 0.8+, разные (даже похожие TTS) ≈ 0.5.
    /// </summary>
    private const float SimilarityThreshold = 0.6f;

    /// <summary>
    /// Короче этого embedding шумный: такой сегмент не создаёт нового спикера
    /// и не обновляет центроид — только прилипает к ближайшему профилю.
    /// </summary>
    private const double MinEnrollSeconds = 2.0;

    /// <summary>Профиль спикера: бегущая сумма L2-нормированных embedding'ов.</summary>
    private sealed class Profile(string name, float[] first)
    {
        public string Name { get; } = name;
        public float[] Sum { get; } = first;
        public int Count { get; private set; } = 1;

        public void Accumulate(float[] emb)
        {
            for (int i = 0; i < Sum.Length; i++) Sum[i] += emb[i];
            Count++;
        }

        /// <summary>Косинус между нормированным вектором и центроидом (Sum/|Sum|).</summary>
        public float Similarity(float[] normalized)
        {
            double dot = 0, norm = 0;
            for (int i = 0; i < Sum.Length; i++)
            {
                dot += Sum[i] * normalized[i];
                norm += Sum[i] * Sum[i];
            }
            return norm <= 0 ? 0 : (float)(dot / Math.Sqrt(norm));
        }
    }

    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly List<Profile> _profiles = new();
    private readonly LanguageProfile _language;
    private string _lastSpeaker;

    /// <summary>Косинусная близость последнего Identify к лучшему профилю (для калибровки порога).</summary>
    public float LastBestScore { get; private set; }

    public int SpeakerCount => _profiles.Count;

    /// <param name="language">Определяет метки спикеров: «Собеседник N» или «Speaker N».</param>
    public SpeakerDiarizer(string modelPath, LanguageProfile? language = null)
    {
        _language = language ?? Languages.Russian;
        _lastSpeaker = _language.NumberedOther(1);

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = 1,
            Provider = "cpu"
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    /// <summary>Возвращает имя спикера для речевого сегмента 16 kHz mono.</summary>
    public string Identify(float[] samples16k)
    {
        var emb = ComputeEmbedding(samples16k);
        if (emb is null) return _lastSpeaker; // сегмент слишком короткий даже для embedding'а

        Normalize(emb);

        Profile? best = null;
        float bestScore = float.MinValue;
        foreach (var p in _profiles)
        {
            float score = p.Similarity(emb);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        LastBestScore = best is null ? 0 : bestScore;

        bool canEnroll = samples16k.Length >= MinEnrollSeconds * Rate;

        if (best is not null && bestScore >= SimilarityThreshold)
        {
            if (canEnroll) best.Accumulate(emb); // короткий сегмент профиль не уточняет
            return _lastSpeaker = best.Name;
        }

        // Незнакомый голос: длинный сегмент (или пустой реестр) заводит нового спикера,
        // короткий — прилипает к ближайшему, чтобы шумный embedding не плодил фантомов.
        if (best is null || canEnroll)
        {
            var profile = new Profile(_language.NumberedOther(_profiles.Count + 1), emb);
            _profiles.Add(profile);
            return _lastSpeaker = profile.Name;
        }
        return _lastSpeaker = best.Name;
    }

    /// <summary>Embedding сегмента, или null если сегмент короче окна модели.</summary>
    private float[]? ComputeEmbedding(float[] samples)
    {
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(Rate, samples);
        stream.InputFinished();
        return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
    }

    private static void Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        if (norm <= 0) return;
        float inv = (float)(1 / Math.Sqrt(norm));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() => _extractor.Dispose();
}
