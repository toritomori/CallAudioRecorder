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
    /// Короче этого embedding шумный: такой сегмент не обновляет центроид
    /// и в консолидации надёжным не считается — только прилипает к ближайшему профилю.
    /// </summary>
    private const double MinEnrollSeconds = 2.0;

    /// <summary>
    /// Ниже этой близости к лучшему профилю голос считается новым. Между ней и
    /// <see cref="SimilarityThreshold"/> — серая зона: сегмент прилипает к ближайшему профилю,
    /// но центроид не уточняет и надёжным не считается.
    /// Замерено --diar-file на записи от 15.09 (четыре участника): настоящие новые голоса пришли
    /// с близостью 0.24–0.37, а фантомы живого решения — с 0.45–0.57, у самого порога 0.6
    /// (единственный на 0.446 отсекает <see cref="MinNewSpeakerSeconds"/>: его кусок — 2.4 с).
    /// Выше 0.5 фантомы вернутся; ниже 0.40 опасно — центроиды разных спикеров лежат на 0.40–0.46,
    /// и новый голос, похожий на знакомый, прилипнет к нему насовсем.
    /// </summary>
    public static float NewSpeakerThreshold { get; set; } = 0.45f;

    /// <summary>
    /// Со скольких секунд сегмент может завести нового спикера — строже, чем уточнить знакомого.
    /// На записи от 15.09 семь фантомов из одиннадцати завели куски по 2.1–2.7 с, а настоящие
    /// голоса впервые звучали кусками по 3.9–11.6 с.
    /// </summary>
    public static double MinNewSpeakerSeconds { get; set; } = 3.0;

    /// <summary>
    /// Насколько должны быть близки центроиды двух профилей, чтобы слить их после записи.
    /// Замерено на часовой записи (--diar-file): разные крупные спикеры дают 0.40–0.46,
    /// поэтому 0.60 их не тронет. Настраивается только ради калибровки самотестом.
    /// </summary>
    public static float MergeThreshold { get; set; } = 0.60f;

    /// <summary>
    /// Порог для профиля, набравшего меньше <see cref="MinReliableSegments"/> надёжных сегментов:
    /// осколок к «своему» спикеру лежит на 0.50–0.64, так что его прижимаем мягче.
    /// </summary>
    public static float WeakMergeThreshold { get; set; } = 0.48f;

    /// <summary>Со скольких надёжных сегментов профиль считается настоящим спикером, а не осколком.</summary>
    private const int MinReliableSegments = 3;

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

    /// <summary>Разобранный сегмент: чей embedding, к какому профилю его отнесли и надёжен ли он.</summary>
    private sealed record Segment(float[] Embedding, int Profile, bool Reliable);

    private readonly SpeakerEmbeddingExtractor? _extractor;
    private readonly List<Profile> _profiles = new();
    private readonly List<Segment> _segments = new();
    private readonly LanguageProfile _language;
    private string _lastSpeaker;

    /// <summary>Косинусная близость последнего Identify к лучшему профилю (для калибровки порога).</summary>
    public float LastBestScore { get; private set; }

    public int SpeakerCount => _profiles.Count;

    /// <param name="language">Определяет метки спикеров: «Собеседник N» или «Speaker N».</param>
    public SpeakerDiarizer(string modelPath, LanguageProfile? language = null) : this(language)
    {
        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = 1,
            Provider = "cpu"
        };
        _extractor = new SpeakerEmbeddingExtractor(config);
    }

    /// <summary>Без модели: embedding'и подаются готовыми через <see cref="Assign"/> (для тестов).</summary>
    internal SpeakerDiarizer(LanguageProfile? language)
    {
        _language = language ?? Languages.Russian;
        _lastSpeaker = _language.NumberedOther(1);
    }

    /// <summary>Возвращает имя спикера для речевого сегмента 16 kHz mono.</summary>
    public string Identify(float[] samples16k)
    {
        var emb = ComputeEmbedding(samples16k);
        if (emb is null) return _lastSpeaker; // сегмент слишком короткий даже для embedding'а
        return Assign(emb, samples16k.Length / (double)Rate);
    }

    /// <summary>Решение по готовому embedding'у сегмента длиной <paramref name="seconds"/>.</summary>
    internal string Assign(float[] emb, double seconds)
    {
        Normalize(emb);

        Profile? best = null;
        float bestScore = float.MinValue;
        foreach (var p in _profiles)
        {
            float score = p.Similarity(emb);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        LastBestScore = best is null ? 0 : bestScore;

        bool canEnroll = seconds >= MinEnrollSeconds;

        if (best is not null && bestScore >= SimilarityThreshold)
        {
            if (canEnroll) best.Accumulate(emb); // короткий сегмент профиль не уточняет
            Remember(emb, _profiles.IndexOf(best), canEnroll);
            return _lastSpeaker = best.Name;
        }

        // Нового спикера заводит только уверенно чужой и достаточно длинный сегмент (или пустой
        // реестр). Короткий или попавший в серую зону прилипает к ближайшему, не трогая центроид:
        // именно такие куски — шумные, смешанные с чужой репликой — и плодили фантомов.
        if (best is null || (bestScore < NewSpeakerThreshold && seconds >= MinNewSpeakerSeconds))
        {
            var profile = new Profile(_language.NumberedOther(_profiles.Count + 1), emb);
            _profiles.Add(profile);
            Remember(emb, _profiles.Count - 1, canEnroll);
            return _lastSpeaker = profile.Name;
        }

        Remember(emb, _profiles.IndexOf(best), reliable: false);
        return _lastSpeaker = best.Name;
    }

    private void Remember(float[] embedding, int profile, bool reliable) =>
        _segments.Add(new Segment(embedding, profile, reliable));

    /// <summary>
    /// Пересобирает профили по всем накопленным embedding'ам и возвращает карту
    /// «старая метка → новая». Пустая карта — ничего не поменялось.
    ///
    /// Зачем: живая диаризация решает по первому же сегменту и назад не смотрит, поэтому
    /// плодит фантомов — на корпусе из 30 записей 39 % профилей набрали не больше двух реплик,
    /// а «собеседников» выходило 8.6 на встречу при примерно пяти реальных. После записи
    /// сравнивать уже есть с чем: центроид, посчитанный по всем сегментам спикера, надёжнее
    /// того, что был в момент решения, и близкие профили сливаются.
    /// </summary>
    public IReadOnlyDictionary<string, string> Consolidate()
    {
        var map = new Dictionary<string, string>();
        if (_profiles.Count < 2 || _segments.Count == 0) return map;

        // Кластер = исходный профиль; сливаем их, пока находится достаточно близкая пара.
        var clusters = new List<List<int>>();
        for (int i = 0; i < _profiles.Count; i++) clusters.Add([i]);

        // Настоящий спикер — профиль, который сам набрал MinReliableSegments надёжных сегментов,
        // а не в сумме со слипшимися осколками.
        var strong = new bool[_profiles.Count];
        for (int i = 0; i < strong.Length; i++) strong[i] = Weight([i]) >= MinReliableSegments;

        while (clusters.Count > 1)
        {
            // Ищем лучшую пару среди ДОПУСТИМЫХ: порог у каждой свой, поэтому обрывать перебор
            // на глобальном максимуме нельзя — пара крупных профилей с близостью 0.56 закрыла бы
            // дорогу осколку, которому до его спикера 0.51 и который слить как раз нужно.
            float bestScore = float.MinValue;
            int a = -1, b = -1;
            for (int i = 0; i < clusters.Count; i++)
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    float score = Cosine(Centroid(clusters[i]), Centroid(clusters[j]));
                    if (score <= bestScore) continue;

                    // Профиль на одной-двух репликах — это чаще всего сегмент, отколовшийся
                    // от настоящего спикера, поэтому его прижимаем к соседу мягче обычного. Но только
                    // к настоящему: осколки похожи друг на друга сильнее, чем на кого-либо (0.60–0.64
                    // на записи от 15.09 — это смешанные и шумные куски), и, слипаясь, набирали вес
                    // настоящего спикера — выходил отдельный фантом на 30 реплик.
                    bool strongI = clusters[i].Exists(p => strong[p]);
                    bool strongJ = clusters[j].Exists(p => strong[p]);
                    if (!strongI && !strongJ) continue;
                    if (score < (strongI && strongJ ? MergeThreshold : WeakMergeThreshold)) continue;

                    bestScore = score;
                    a = i;
                    b = j;
                }

            if (a < 0) break; // допустимых пар не осталось

            clusters[a].AddRange(clusters[b]);
            clusters.RemoveAt(b);
        }

        if (clusters.Count == _profiles.Count) return map;

        // Нумеруем заново по порядку появления: «Собеседник 1» — тот, кто заговорил первым.
        clusters.Sort((x, y) => FirstSegment(x).CompareTo(FirstSegment(y)));
        for (int i = 0; i < clusters.Count; i++)
        {
            var name = _language.NumberedOther(i + 1);
            foreach (var profile in clusters[i])
                if (_profiles[profile].Name != name) map[_profiles[profile].Name] = name;
        }
        return map;
    }

    /// <summary>Центроид кластера по надёжным сегментам (если их нет — по всем).</summary>
    private float[] Centroid(List<int> cluster)
    {
        var sum = new float[_segments[0].Embedding.Length];
        int count = 0;
        foreach (var reliableOnly in new[] { true, false })
        {
            foreach (var s in _segments)
            {
                if (!cluster.Contains(s.Profile)) continue;
                if (reliableOnly && !s.Reliable) continue;
                for (int i = 0; i < sum.Length; i++) sum[i] += s.Embedding[i];
                count++;
            }
            if (count > 0) break; // надёжных хватило — по ненадёжным пересчитывать не нужно
        }
        if (count > 0) Normalize(sum);
        return sum;
    }

    /// <summary>Сколько надёжных сегментов набрал кластер.</summary>
    private int Weight(List<int> cluster)
    {
        int count = 0;
        foreach (var s in _segments)
            if (s.Reliable && cluster.Contains(s.Profile)) count++;
        return count;
    }

    private int FirstSegment(List<int> cluster)
    {
        for (int i = 0; i < _segments.Count; i++)
            if (cluster.Contains(_segments[i].Profile)) return i;
        return int.MaxValue;
    }

    /// <summary>
    /// Попарная близость центроидов исходных профилей — диагностика для калибровки порогов.
    /// Возвращает пары «имя A, имя B, косинус, число надёжных сегментов A и B».
    /// </summary>
    public IEnumerable<(string A, string B, float Score, int WeightA, int WeightB)> ProfileDistances()
    {
        for (int i = 0; i < _profiles.Count; i++)
            for (int j = i + 1; j < _profiles.Count; j++)
                yield return (_profiles[i].Name, _profiles[j].Name,
                    Cosine(Centroid([i]), Centroid([j])), Weight([i]), Weight([j]));
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return (float)dot; // оба вектора нормированы
    }

    /// <summary>Embedding сегмента, или null если сегмент короче окна модели.</summary>
    private float[]? ComputeEmbedding(float[] samples)
    {
        var extractor = _extractor ?? throw new InvalidOperationException("Диаризатор создан без модели.");
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(Rate, samples);
        stream.InputFinished();
        return extractor.IsReady(stream) ? extractor.Compute(stream) : null;
    }

    private static void Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        if (norm <= 0) return;
        float inv = (float)(1 / Math.Sqrt(norm));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() => _extractor?.Dispose();
}
