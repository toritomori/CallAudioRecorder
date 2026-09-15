using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Решения диаризатора на готовых embedding'ах (модель не нужна). Голос — единичный вектор:
/// спикеры A и B лежат на разных осях, а близость сегмента к ним задаётся долей их оси.
/// Пороги — из --diar-file на записи от 15.09 (четыре участника, живое решение давало 15
/// профилей): настоящие новые голоса приходили с близостью 0.24–0.39, фантомы — с 0.45–0.59.
/// </summary>
public class SpeakerDiarizerTests
{
    private const int Dim = 16;
    private const double Long = 5.0;

    [Fact]
    public void GreyZoneSegment_StaysWithNearestSpeaker()
    {
        // Близость 0.5 — ниже порога «знакомый голос», но выше порога «новый»: такие куски
        // (шумные, смешанные с чужой репликой) и заводили фантомов.
        var diarizer = WithSpeakerA();

        Assert.Equal("Собеседник 1", diarizer.Assign(Voice((0, 0.5), (5, 1)), Long));
        Assert.Equal(1, diarizer.SpeakerCount);
    }

    [Fact]
    public void DistinctVoice_CreatesSpeaker()
    {
        var diarizer = WithSpeakerA();

        Assert.Equal("Собеседник 2", diarizer.Assign(Voice((0, 0.3), (1, 1)), Long));
        Assert.Equal(2, diarizer.SpeakerCount);
    }

    [Fact]
    public void ShortDistinctSegment_DoesNotCreateSpeaker()
    {
        // 2.5 с — длины хватает уточнить знакомого, но не завести нового: семь фантомов
        // из одиннадцати на записи от 15.09 завели куски по 2.1–2.7 с.
        var diarizer = WithSpeakerA();

        Assert.Equal("Собеседник 1", diarizer.Assign(Voice((1, 1)), 2.5));
        Assert.Equal(1, diarizer.SpeakerCount);
    }

    [Fact]
    public void Consolidate_FragmentsMergeIntoSpeaker_NotIntoPhantom()
    {
        // Осколки похожи друг на друга (0.55) сильнее, чем на своего спикера (0.5). Раньше
        // они слипались первыми, набирали вес настоящего спикера и оставались фантомом.
        float saved = SpeakerDiarizer.NewSpeakerThreshold;
        SpeakerDiarizer.NewSpeakerThreshold = 0.6f; // прежнее живое решение: заводит осколки
        try
        {
            var diarizer = WithSpeakerA();
            for (int i = 0; i < 3; i++) diarizer.Assign(Voice((1, 1)), Long); // B
            for (int i = 0; i < 3; i++)
                diarizer.Assign(Voice((0, 0.5), (2, Math.Sqrt(0.30)), (3 + i, Math.Sqrt(0.45))), Long);
            Assert.Equal(5, diarizer.SpeakerCount);

            var renames = diarizer.Consolidate();

            Assert.Equal("Собеседник 1", renames["Собеседник 3"]);
            Assert.Equal("Собеседник 1", renames["Собеседник 4"]);
            Assert.Equal("Собеседник 1", renames["Собеседник 5"]);
            Assert.False(renames.ContainsKey("Собеседник 2"));
        }
        finally
        {
            SpeakerDiarizer.NewSpeakerThreshold = saved;
        }
    }

    /// <summary>Диаризатор, уже знающий спикера A по трём длинным сегментам.</summary>
    private static SpeakerDiarizer WithSpeakerA()
    {
        var diarizer = new SpeakerDiarizer(Languages.Russian);
        for (int i = 0; i < 3; i++) diarizer.Assign(Voice((0, 1)), Long);
        return diarizer;
    }

    /// <summary>Вектор из долей по осям; диаризатор нормирует его сам.</summary>
    private static float[] Voice(params (int Axis, double Weight)[] parts)
    {
        var v = new float[Dim];
        foreach (var (axis, weight) in parts) v[axis] = (float)weight;

        // Доля по оси 0 должна остаться косинусом к спикеру A, поэтому хвост добиваем до единичной длины.
        double norm = v.Sum(x => (double)x * x);
        if (parts.Length > 1 && norm > 1)
        {
            double head = (double)v[parts[0].Axis] * v[parts[0].Axis];
            double scale = Math.Sqrt((1 - head) / (norm - head));
            foreach (var (axis, _) in parts.Skip(1)) v[axis] *= (float)scale;
        }
        return v;
    }
}
