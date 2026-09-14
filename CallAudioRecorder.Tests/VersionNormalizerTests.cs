using CallAudioRecorder.Models;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Восстановление номеров версий, съеденных распознаванием («1.8.3» → «183»). Нормализация идёт
/// по записи целиком: семейство версий подтверждается в одном месте, а применяется везде.
/// Правила против ложных срабатываний — из реальных записей («10 000» давало «0.0.0»,
/// «сто процентов» — «1.0.0»).
/// </summary>
public class VersionNormalizerTests
{
    private static List<TranscriptEntry> Meeting(params string[] lines) =>
        lines.Select((text, i) => new TranscriptEntry("Я", text, TimeSpan.FromSeconds(i * 10))).ToList();

    private static List<string> Texts(IEnumerable<TranscriptEntry> entries) => entries.Select(e => e.Text).ToList();

    [Fact]
    public void NumberNextToMarker_Twice_BecomesVersion()
    {
        var result = VersionNormalizer.Normalize(Meeting(
            "версия 183 уже в сторе",
            "в билде 183 поправили оплату"));

        Assert.Equal(["версия 1.8.3 уже в сторе", "в билде 1.8.3 поправили оплату"], Texts(result));
    }

    [Fact]
    public void ConfirmedFamily_AppliesEverywhere_EvenWithoutMarker()
    {
        // Каноническое «1.8.3» подтверждает семейство 1.8.x само по себе — дальше «180»
        // приводится к версии и там, где слова «версия» рядом нет.
        var result = VersionNormalizer.Normalize(Meeting(
            "выкатываем 1.8.3 в четверг",
            "а 180 откатили"));

        Assert.Equal(["выкатываем 1.8.3 в четверг", "а 1.8.0 откатили"], Texts(result));
    }

    [Fact]
    public void SingleMarkerNeighbourhood_IsNotEnough()
    {
        // Одно совпадение рядом с «релизом» — чаще просто число: нужно второе соседство
        // с маркером или пять упоминаний.
        var entries = Meeting("релиз готов на 100 процентов", "осталось 183 задачи");
        Assert.Same(entries, VersionNormalizer.Normalize(entries));
    }

    [Fact]
    public void RepeatedNumber_WithOneMarker_BecomesVersion()
    {
        var entries = Meeting(
            "в релизе 183 всё хорошо", "183 уже на проде", "по 183 жалоб нет",
            "183 обновили", "про 183 ещё раз");

        Assert.All(VersionNormalizer.Normalize(entries), e => Assert.Contains("1.8.3", e.Text));
    }

    [Fact]
    public void ThousandsWrittenWithSpace_AreLeftAlone()
    {
        // «10 000» раньше давало «0.0.0»: хвост большого числа за версию не считается,
        // даже когда семейство 1.0.x на встрече подтверждено.
        var result = VersionNormalizer.Normalize(Meeting(
            "версия 1.0.0 собрана",
            "релиз принёс 10 000 установок"));

        Assert.Equal("релиз принёс 10 000 установок", result[1].Text);
    }

    [Fact]
    public void LeadingZero_IsNeverAVersion()
    {
        var entries = Meeting("версия 083 странная", "в билде 083 то же самое");
        Assert.Same(entries, VersionNormalizer.Normalize(entries));
    }

    [Fact]
    public void ChangedEntry_KeepsOriginalInRawText()
    {
        var log = new List<(string From, string To)>();
        var result = VersionNormalizer.Normalize(Meeting("версия 183", "билд 183"), log);

        Assert.Equal("версия 183", result[0].RawText);
        Assert.Equal([("183", "1.8.3"), ("183", "1.8.3")], log);
    }
}
