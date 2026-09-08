using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>
/// Формирование итогов встречи с оглядкой на окно контекста модели.
///
/// Зачем: часовой разговор — это ~40 000 токенов транскрипта. Локальная модель столько за раз
/// не примет, а Ollama промпт длиннее окна молча урезает (вместе с системной инструкцией) —
/// и на выходе получаются несколько строк не по формату. Поэтому длинная встреча идёт в два
/// прохода: конспект каждой части, затем итоги по конспектам. Короткая — как и раньше, одним
/// запросом; облачные модели с их сотнями тысяч токенов сюда практически не попадают.
/// </summary>
public static class SummaryComposer
{
    /// <summary>
    /// Стримит итоги встречи. Для длинных встреч сперва молча конспектирует части,
    /// сообщая о прогрессе через <paramref name="stage"/>, и стримит только финальный проход.
    /// </summary>
    /// <param name="glossary">
    /// Термины, имена и названия встречи. Идут в промпт обоих проходов: распознавание разносит
    /// редкое название на десяток написаний, и без списка этот разнобой протекает в итоги
    /// (вплоть до двух «разных» ответственных из одной фамилии).
    /// </param>
    public static async IAsyncEnumerable<string> ComposeAsync(
        OllamaClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        IProgress<string>? stage = null, ChatOutcome? outcome = null, LanguageProfile? language = null,
        string? glossary = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        entries = WithoutFillers(entries);

        var systemPrompt = SummaryPrompt.SystemFor(language);
        int budget = await ollama.GetInputBudgetAsync(model, SummaryPrompt.ResponseTokens, ct);
        var whole = SummaryPrompt.BuildUserMessage(entries, language, glossary);

        string finalMessage;
        if (Fits(systemPrompt, whole, budget))
        {
            finalMessage = whole;
        }
        else
        {
            int partBudget = await PartBudgetAsync(ollama, model, language, glossary, ct);
            var chunks = SplitByBudget(entries, partBudget);

            var parts = new List<string>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                stage?.Report($"Конспектирую часть {i + 1} из {chunks.Count}…");
                parts.Add(await ollama.ChatAsync(
                    model, SummaryPrompt.PartSystemFor(language),
                    SummaryPrompt.BuildPartMessage(chunks[i], i + 1, chunks.Count, language, glossary),
                    SummaryPrompt.PartResponseTokens, ct: ct));
            }

            parts = await CondenseAsync(ollama, model, parts, budget, partBudget, stage, language, glossary, ct);
            stage?.Report("Свожу итоги встречи…");
            finalMessage = SummaryPrompt.BuildFromParts(parts, language, glossary);
        }

        await foreach (var chunk in ollama.ChatStreamAsync(
            model, systemPrompt, finalMessage, SummaryPrompt.ResponseTokens, outcome, ct))
            yield return chunk;
    }

    /// <summary>
    /// Реплики-поддакивания («угу», «ага», «э-э») в итогах не участвуют: смысла не несут,
    /// а места занимают — на корпусе это каждая двадцатая реплика.
    ///
    /// Согласия и отказы («да», «нет», «хорошо», «понял») ОСТАЮТСЯ: короткое «да» бывает
    /// ответом на вопрос, от которого зависит решение встречи, и выкидывать его нельзя.
    /// </summary>
    private static readonly Regex Filler = new(
        @"^(?:угу|ага|мгм|м+|э+|а+|ам|эм|ну|вот|так|uh+|um+|mm+|yeah|uh-huh)[\s.,!?…-]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<TranscriptEntry> WithoutFillers(IReadOnlyList<TranscriptEntry> entries)
    {
        var kept = entries.Where(e => !Filler.IsMatch(e.Text.Trim())).ToList();
        return kept.Count == 0 ? entries : kept; // всё выкинуть нельзя — лучше отдать как есть
    }

    /// <summary>
    /// Схлопывает конспекты, пока они сами не начнут влезать в окно вместе с системным промптом.
    /// Нужно только для очень длинных встреч на модели с маленьким окном.
    /// </summary>
    private static async Task<List<string>> CondenseAsync(
        OllamaClient ollama, string model, List<string> parts, int budget, int partBudget,
        IProgress<string>? stage, LanguageProfile? language, string? glossary, CancellationToken ct)
    {
        while (parts.Count > 1 &&
               !Fits(SummaryPrompt.SystemFor(language),
                     SummaryPrompt.BuildFromParts(parts, language, glossary), budget))
        {
            var groups = GroupByBudget(parts, partBudget);
            if (groups.Count >= parts.Count) break; // дальше не ужимается — отдаём как есть

            var condensed = new List<string>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                stage?.Report($"Сжимаю конспекты: {i + 1} из {groups.Count}…");
                condensed.Add(await ollama.ChatAsync(
                    model, SummaryPrompt.PartSystemFor(language),
                    (language?.Language == TranscriptionLanguage.English
                        ? "Merge the summaries of adjacent meeting fragments into one, losing nothing:\n\n"
                        : "Объедини конспекты соседних фрагментов встречи в один, ничего не теряя:\n\n") +
                    string.Join("\n\n", groups[i]),
                    SummaryPrompt.PartResponseTokens, ct: ct));
            }
            parts = condensed;
        }
        return parts;
    }

    private static bool Fits(string systemPrompt, string userMessage, int budget) =>
        OllamaClient.EstimateTokens(systemPrompt) + OllamaClient.EstimateTokens(userMessage) <= budget;

    /// <summary>Сколько токенов транскрипта класть в одну часть первого прохода.</summary>
    private static async Task<int> PartBudgetAsync(OllamaClient ollama, string model,
        LanguageProfile? language, string? glossary, CancellationToken ct)
    {
        int budget = await ollama.GetInputBudgetAsync(model, SummaryPrompt.PartResponseTokens, ct);
        // Оставляем место под системный промпт, глоссарий и шапку «Фрагмент N из M»,
        // плюс запас на неточность оценки: глоссарий повторяется в каждой части.
        int service = OllamaClient.EstimateTokens(SummaryPrompt.PartSystemFor(language))
                    + OllamaClient.EstimateTokens(SummaryPrompt.BuildGlossaryBlock(glossary, language));
        int usable = (int)((budget - service - 128) * 0.9);
        return Math.Max(1024, Math.Min(usable, 24576)); // части крупнее 24k токенов модели уже плохо держат
    }

    private static List<List<TranscriptEntry>> SplitByBudget(IReadOnlyList<TranscriptEntry> entries, int budget)
    {
        var result = new List<List<TranscriptEntry>>();
        var current = new List<TranscriptEntry>();
        int tokens = 0;

        foreach (var entry in entries)
        {
            int size = OllamaClient.EstimateTokens(SummaryPrompt.FormatEntry(entry));
            if (current.Count > 0 && tokens + size > budget)
            {
                result.Add(current);
                current = [];
                tokens = 0;
            }
            current.Add(entry);
            tokens += size;
        }

        if (current.Count > 0) result.Add(current);
        return result;
    }

    private static List<List<string>> GroupByBudget(List<string> texts, int budget)
    {
        var result = new List<List<string>>();
        var current = new List<string>();
        int tokens = 0;

        foreach (var text in texts)
        {
            int size = OllamaClient.EstimateTokens(text);
            if (current.Count > 0 && tokens + size > budget)
            {
                result.Add(current);
                current = [];
                tokens = 0;
            }
            current.Add(text);
            tokens += size;
        }

        if (current.Count > 0) result.Add(current);
        return result;
    }
}
