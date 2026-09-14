using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Правила канонизации терминов. Каждое из них появилось из конкретного ложного срабатывания
/// на корпусе транскриптов (CLAUDE.md, раздел TermCanonizer), поэтому тест держит именно
/// эти случаи: ослабил правило — тест покажет, какое слово поехало.
/// </summary>
public class TermCanonizerTests
{
    private const string Glossary =
        "Firebase, AppLovin, Crazy Games, Battle Pass, Poki, DM, UW, SDK, MVP, build, config";

    private static readonly TermCanonizer Canonizer = TermCanonizer.Build(Glossary)!;

    [Theory]
    [InlineData("проверь Firebace до релиза", "проверь Firebase до релиза")]
    [InlineData("купили Батл Пасс на неделю", "купили Battle Pass на неделю")]
    [InlineData("выложим на поки в пятницу", "выложим на Poki в пятницу")]
    [InlineData("реклама через эпловин", "реклама через AppLovin")]
    [InlineData("купили BattlePass, да", "купили Battle Pass, да")]
    public void MangledTerm_BecomesCanonical(string text, string expected) =>
        Assert.Equal(expected, Canonizer.Apply(text));

    /// <summary>
    /// По одному скелету согласных эти слова неотличимы от терминов («задачки» и SDK — оба sdk),
    /// решает звуковая форма с гласными. На первом прогоне без неё ложными были все 4081 замена.
    /// </summary>
    [Theory]
    [InlineData("задачки")]
    [InlineData("разобрать задачки к пятнице")]
    public void SoundFormWithVowels_DecidesNotSkeleton(string text) =>
        Assert.Equal(text, Canonizer.Apply(text));

    /// <summary>
    /// Тот же случай для «были» и build: здесь глоссарий пишет термин с заглавной, чтобы
    /// кириллица вообще имела право на замену, — и отсекает только звуковая форма.
    /// </summary>
    [Fact]
    public void Byli_DoesNotBecomeBuild_EvenWhenBuildIsProperNoun()
    {
        var canonizer = TermCanonizer.Build("Build")!;
        Assert.Equal("мы были на созвоне", canonizer.Apply("мы были на созвоне"));
    }

    /// <summary>
    /// Кириллица заменяется только на термин с заглавной: нарицательное заимствование в русском
    /// склоняется, и «в конфигах» после замены на «config» развалилось бы.
    /// </summary>
    [Theory]
    [InlineData("поправил в конфигах")]
    [InlineData("мы были на созвоне")]
    public void CyrillicWord_IsNotReplacedByLowercaseTerm(string text) =>
        Assert.Equal(text, Canonizer.Apply(text));

    /// <summary>Начало слова должно совпадать — иначе «наполовину» тянется к AppLovin серединой.</summary>
    [Fact]
    public void WordStart_MustMatch() =>
        Assert.Equal("сделали наполовину", Canonizer.Apply("сделали наполовину"));

    /// <summary>«MVP6» — это MVP шестой: цифру терять нельзя, раз в каноне её нет.</summary>
    [Fact]
    public void WordWithDigits_KeepsItsNumber() =>
        Assert.Equal("к MVP6 успеваем", Canonizer.Apply("к MVP6 успеваем"));

    /// <summary>
    /// Короткие термины (Poki, DM, UW) сравниваются только целиком: у «DM» и «Дима» общий скелет,
    /// а «пока» от «Poki» отличает одна гласная.
    /// </summary>
    [Theory]
    [InlineData("Дима, что по задаче?")]
    [InlineData("ну пока всё")]
    [InlineData("сыграли в покер")]
    public void ShortTerm_MatchesOnlyWhole(string text) =>
        Assert.Equal(text, Canonizer.Apply(text));

    /// <summary>
    /// Пара слов берётся, только если оба значимы и разделены пробелом или дефисом:
    /// иначе пара захватывает предлог или соседнее слово через запятую.
    /// </summary>
    [Fact]
    public void Pair_DoesNotSwallowPrepositionOrNeighbour()
    {
        Assert.Equal("на Battle Pass", Canonizer.Apply("на BattlePass"));
        Assert.Equal("купили Battle Pass, да", Canonizer.Apply("купили BattlePass, да"));
    }

    [Fact]
    public void Log_CollectsReplacements()
    {
        var log = new List<(string From, string To)>();
        Canonizer.Apply("проверь Firebace и Батл Пасс", log);
        Assert.Equal([("Firebace", "Firebase"), ("Батл Пасс", "Battle Pass")], log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("миржа, ветка")] // одни кириллические записи — канонизировать нечего
    public void GlossaryWithoutLatinTerms_GivesNoCanonizer(string? glossary) =>
        Assert.Null(TermCanonizer.Build(glossary));
}
