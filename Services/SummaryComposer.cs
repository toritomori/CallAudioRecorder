using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    public static async IAsyncEnumerable<string> ComposeAsync(
        OllamaClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        IProgress<string>? stage = null, ChatOutcome? outcome = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int budget = await ollama.GetInputBudgetAsync(model, SummaryPrompt.ResponseTokens, ct);
        var whole = SummaryPrompt.BuildUserMessage(entries);

        string finalMessage;
        if (Fits(SummaryPrompt.System, whole, budget))
        {
            finalMessage = whole;
        }
        else
        {
            int partBudget = await PartBudgetAsync(ollama, model, ct);
            var chunks = SplitByBudget(entries, partBudget);

            var parts = new List<string>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                stage?.Report($"Конспектирую часть {i + 1} из {chunks.Count}…");
                parts.Add(await ollama.ChatAsync(
                    model, SummaryPrompt.PartSystem,
                    SummaryPrompt.BuildPartMessage(chunks[i], i + 1, chunks.Count),
                    SummaryPrompt.PartResponseTokens, ct: ct));
            }

            parts = await CondenseAsync(ollama, model, parts, budget, partBudget, stage, ct);
            stage?.Report("Свожу итоги встречи…");
            finalMessage = SummaryPrompt.BuildFromParts(parts);
        }

        await foreach (var chunk in ollama.ChatStreamAsync(
            model, SummaryPrompt.System, finalMessage, SummaryPrompt.ResponseTokens, outcome, ct))
            yield return chunk;
    }

    /// <summary>
    /// Схлопывает конспекты, пока они сами не начнут влезать в окно вместе с системным промптом.
    /// Нужно только для очень длинных встреч на модели с маленьким окном.
    /// </summary>
    private static async Task<List<string>> CondenseAsync(
        OllamaClient ollama, string model, List<string> parts,
        int budget, int partBudget, IProgress<string>? stage, CancellationToken ct)
    {
        while (parts.Count > 1 && !Fits(SummaryPrompt.System, SummaryPrompt.BuildFromParts(parts), budget))
        {
            var groups = GroupByBudget(parts, partBudget);
            if (groups.Count >= parts.Count) break; // дальше не ужимается — отдаём как есть

            var condensed = new List<string>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                stage?.Report($"Сжимаю конспекты: {i + 1} из {groups.Count}…");
                condensed.Add(await ollama.ChatAsync(
                    model, SummaryPrompt.PartSystem,
                    "Объедини конспекты соседних фрагментов встречи в один, ничего не теряя:\n\n" +
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
    private static async Task<int> PartBudgetAsync(OllamaClient ollama, string model, CancellationToken ct)
    {
        int budget = await ollama.GetInputBudgetAsync(model, SummaryPrompt.PartResponseTokens, ct);
        // Оставляем место под системный промпт и шапку «Фрагмент N из M», плюс запас на неточность оценки.
        int usable = (int)((budget - OllamaClient.EstimateTokens(SummaryPrompt.PartSystem) - 128) * 0.9);
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
