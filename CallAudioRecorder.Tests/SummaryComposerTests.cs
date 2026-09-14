using CallAudioRecorder.Models;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Итоги с оглядкой на окно контекста: фильтр поддакиваний, разбиение транскрипта по бюджету
/// и выбор между одним запросом и проходом по частям.
/// </summary>
public class SummaryComposerTests
{
    private static List<TranscriptEntry> Lines(params string[] texts) =>
        texts.Select((t, i) => new TranscriptEntry(i % 2 == 0 ? "Я" : "Собеседник 1", t, TimeSpan.FromSeconds(i * 5)))
            .ToList();

    [Fact]
    public void WithoutFillers_DropsBackchannel_KeepsShortAnswers()
    {
        // «Да» и «нет» остаются: короткое «да» бывает ответом, от которого зависит решение.
        var entries = Lines("угу", "Ага.", "мгм", "Э-э...", "да", "Нет.", "хорошо", "понял", "Давайте начнём.");

        var kept = SummaryComposer.WithoutFillers(entries).Select(e => e.Text);

        Assert.Equal(["да", "Нет.", "хорошо", "понял", "Давайте начнём."], kept);
    }

    /// <summary>
    /// Короткие реплики из «а», «ну», «вот», «так» — это вопросы и ответы, а не поддакивание:
    /// «— Может, через кэш? — А так? — Тоже можно». Выкинуть их — потерять ход решения.
    /// </summary>
    [Theory]
    [InlineData("А так?")]
    [InlineData("Ну а так?")]
    [InlineData("Вот так.")]
    [InlineData("Ну так.")]
    [InlineData("Вот так вот.")]
    [InlineData("ну вот")]
    public void WithoutFillers_KeepsShortMeaningfulLines(string text)
    {
        var entries = Lines("Может, через кэш?", text);
        Assert.Contains(text, SummaryComposer.WithoutFillers(entries).Select(e => e.Text));
    }

    [Theory]
    [InlineData("ага, ага")]
    [InlineData("Угу-угу.")]
    [InlineData("Э-э...")]
    [InlineData("ээ, мм")]
    [InlineData("Ну.")]
    public void WithoutFillers_DropsInterjectionChains(string text)
    {
        var entries = Lines("Может, через кэш?", text);
        Assert.DoesNotContain(text, SummaryComposer.WithoutFillers(entries).Select(e => e.Text));
    }

    [Fact]
    public void WithoutFillers_WhenEverythingIsFiller_ReturnsAsIs()
    {
        var entries = Lines("угу", "ага");
        Assert.Same(entries, SummaryComposer.WithoutFillers(entries));
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 200)]
    [InlineData(3, 1000)]
    [InlineData(4, 5)] // бюджет меньше одной реплики: каждая идёт отдельной частью
    public void SplitByBudget_LosesNothing_AndKeepsEveryPartWithinBudget(int seed, int budget)
    {
        var rnd = new Random(seed);
        var entries = Enumerable.Range(0, 300)
            .Select(i => new TranscriptEntry("Я", new string('а', rnd.Next(1, 300)), TimeSpan.FromSeconds(i)))
            .ToList();

        var parts = SummaryComposer.SplitByBudget(entries, budget);

        Assert.Equal(entries, parts.SelectMany(p => p));
        Assert.All(parts, part =>
        {
            int tokens = part.Sum(e => OllamaClient.EstimateTokens(SummaryPrompt.FormatEntry(e)));
            Assert.True(part.Count == 1 || tokens <= budget, $"часть из {part.Count} реплик: {tokens} > {budget}");
        });
    }

    [Fact]
    public async Task ShortMeeting_GoesAsOneRequest()
    {
        var model = new FakeChatClient("## Краткое резюме\nВсё решили.");
        var stages = new List<string>();

        var answer = await Collect(SummaryComposer.ComposeAsync(model, "fake", Lines("Начнём.", "Давай."),
            new SyncProgress(stages.Add), glossary: "Firebase"));

        Assert.Equal("## Краткое резюме\nВсё решили.", answer);
        var call = Assert.Single(model.Calls);
        Assert.Contains("Firebase", call.User);
        Assert.Empty(stages);
    }

    [Fact]
    public async Task LongMeeting_GoesByParts_WithGlossaryInEveryPart()
    {
        // Окно поменьше: ~14 000 токенов транскрипта в него целиком не влезают.
        var model = new FakeChatClient(c => c.System == SummaryPrompt.PartSystemFor(null)
            ? "- Факт: конспект части"
            : "## Краткое резюме\nИтоги по частям.") { InputBudget = 12_000 };
        var stages = new List<string>();
        var entries = Enumerable.Range(0, 400)
            .Select(i => new TranscriptEntry("Я", $"Реплика номер {i}: обсуждаем интеграцию и сроки релиза.",
                TimeSpan.FromSeconds(i * 5)))
            .ToList();

        var answer = await Collect(SummaryComposer.ComposeAsync(
            model, "fake", entries, new SyncProgress(stages.Add), glossary: "Firebase"));

        Assert.Equal("## Краткое резюме\nИтоги по частям.", answer);
        var parts = model.Calls.Where(c => c.System == SummaryPrompt.PartSystemFor(null)).ToList();
        Assert.True(parts.Count >= 2, $"частей: {parts.Count}");
        Assert.All(parts, p => Assert.Contains("Firebase", p.User));

        // Каждая реплика попала ровно в одну часть — ничего не потеряно на стыках.
        Assert.All(entries, e => Assert.Single(parts, p => p.User.Contains(e.Text + Environment.NewLine)));

        var final = model.Calls[^1];
        Assert.Equal(SummaryPrompt.SystemFor(null), final.System);
        Assert.Contains($"### Часть {parts.Count} из {parts.Count}", final.User);
        Assert.Contains($"Конспектирую часть 1 из {parts.Count}…", stages);
        Assert.Equal("Свожу итоги встречи…", stages[^1]);
    }

    [Fact]
    public async Task CloudModel_TakesWholeMeetingAtOnce()
    {
        var model = new FakeChatClient("итоги") { InputBudget = int.MaxValue };
        var entries = Enumerable.Range(0, 400)
            .Select(i => new TranscriptEntry("Я", $"Реплика {i} про интеграцию и сроки.", TimeSpan.FromSeconds(i)))
            .ToList();

        await Collect(SummaryComposer.ComposeAsync(model, "fake", entries));

        Assert.Single(model.Calls);
    }

    [Fact]
    public async Task ContextLimitHit_IsReportedThroughOutcome()
    {
        var model = new FakeChatClient("## Краткое резюме\nОборвало") { DoneReason = "length" };
        var outcome = new ChatOutcome();

        await Collect(SummaryComposer.ComposeAsync(model, "fake", Lines("Начнём."), outcome: outcome));

        Assert.True(outcome.HitContextLimit);
    }

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var parts = new List<string>();
        await foreach (var chunk in stream) parts.Add(chunk);
        return string.Concat(parts);
    }

    /// <summary><see cref="Progress{T}"/> шлёт отчёты через контекст синхронизации — здесь нужен порядок.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
