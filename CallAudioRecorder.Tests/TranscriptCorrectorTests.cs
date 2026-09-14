using System.Text.RegularExpressions;
using CallAudioRecorder.Models;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// LLM-коррекция транскрипта: ответ разбирается по формату «[номер] текст», и что бы модель
/// ни ответила вне формата, транскрипт не должен испортиться.
/// </summary>
public class TranscriptCorrectorTests
{
    private static readonly List<TranscriptEntry> Raw =
    [
        new("Я", "эйпиай отдаёт пятисотку", TimeSpan.FromSeconds(5)),
        new("Собеседник", "посмотрю пул реквест", TimeSpan.FromSeconds(15)),
    ];

    [Fact]
    public async Task AnswerInFormat_ReplacesTextKeepsSpeakers()
    {
        var model = new FakeChatClient("[0] API отдаёт 500\n[1] посмотрю pull request");

        var result = await TranscriptCorrector.CorrectAsync(model, "fake", Raw, "API, pull request");

        Assert.Equal(["Я: API отдаёт 500", "Собеседник: посмотрю pull request"],
            result.Select(e => $"{e.Speaker}: {e.Text}"));
    }

    [Fact]
    public async Task AnswerOutOfFormat_ReturnsTranscriptUntouched()
    {
        var model = new FakeChatClient("Конечно! Вот исправленный текст: API отдаёт 500, посмотрю pull request.");

        var result = await TranscriptCorrector.CorrectAsync(model, "fake", Raw, "");

        Assert.Same(Raw, result);
    }

    [Fact]
    public async Task PartialAnswer_FixesOnlyReturnedLines()
    {
        var model = new FakeChatClient("[1] посмотрю pull request");

        var result = await TranscriptCorrector.CorrectAsync(model, "fake", Raw, "");

        Assert.Equal(["эйпиай отдаёт пятисотку", "посмотрю pull request"], result.Select(e => e.Text));
    }

    [Fact]
    public async Task Glossary_GoesIntoPrompt()
    {
        var model = new FakeChatClient("[0] ok");

        await TranscriptCorrector.CorrectAsync(model, "fake", Raw, "Kubernetes, GitHub Actions");

        Assert.Contains("Kubernetes, GitHub Actions", Assert.Single(model.Calls).User);
    }

    [Fact]
    public async Task LongTranscript_EveryBatchFitsMinimalWindow()
    {
        // Окно решает, сколько слоёв модели останется в видеопамяти: на 8 ГБ qwen3.5:9b при окне
        // 20480 теряла три слоя из 34 и генерировала вдвое медленнее. Партия — промпт, глоссарий,
        // реплики и равный им ответ — обязана уложиться в минимальное окно.
        var entries = Enumerable.Range(0, 600)
            .Select(i => new TranscriptEntry(i % 2 == 0 ? "Я" : "Собеседник 1",
                $"реплика {i}: " + string.Concat(Enumerable.Repeat("слово ", 16)), TimeSpan.FromSeconds(i * 8)))
            .ToList();
        string glossary = string.Join(", ", Enumerable.Range(0, 150).Select(i => $"Термин{i}"));
        var model = new FakeChatClient(call => string.Join("\n",
            Regex.Matches(call.User, @"^\[(\d+)\]", RegexOptions.Multiline).Select(m => $"[{m.Groups[1].Value}] исправлено")))
        {
            InputBudget = OllamaClient.LocalContextCap,
        };

        var result = await TranscriptCorrector.CorrectAsync(model, "fake", entries, glossary);

        Assert.True(model.Calls.Count > 1);
        Assert.All(model.Calls, call =>
        {
            int need = OllamaClient.EstimateTokens(call.System) + OllamaClient.EstimateTokens(call.User) + call.ResponseTokens;
            Assert.True(need <= OllamaClient.MinContext, $"запрос на {need} токенов не влезает в {OllamaClient.MinContext}");
            var lines = call.User[call.User.IndexOf("\n[", StringComparison.Ordinal)..];
            Assert.True(call.ResponseTokens >= OllamaClient.EstimateTokens(lines), "ответу не хватит резерва");
        });
        Assert.All(result, e => Assert.Equal("исправлено", e.Text));
    }

    [Fact]
    public void Hotwords_KeepOnlyModelAlphabet()
    {
        // GigaAM почти не знает латиницы, Parakeet — кириллицы: чужой алфавит не смещает ничего.
        const string glossary = "Firebase, миржа, Crazy Games; поки";

        Assert.Equal(["миржа", "поки"], TranscriptCorrector.ToHotwords(glossary, Languages.Russian));
        Assert.Equal(["firebase", "crazy games"], TranscriptCorrector.ToHotwords(glossary, Languages.English));
    }
}
