using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>
/// Исправление ошибок распознавания с помощью LLM.
///
/// Зачем: словарь GigaAM почти не содержит латиницы (85 токенов из 1025), поэтому
/// английские термины неизбежно приходят искажёнными («эйпиай», «дед лайн»).
/// Ни один параметр распознавателя это не чинит — нужна модель с языковым контекстом.
/// LLM видит весь диалог целиком и восстанавливает термины, имена и пунктуацию.
///
/// Важно: здесь ответ по объёму равен входу, поэтому в окно модели должны влезть ОБА.
/// Длинный транскрипт идёт партиями — иначе Ollama урежет промпт, а генерация оборвётся
/// на первых репликах (done_reason = "length").
/// </summary>
public static class TranscriptCorrector
{
    private const string SystemPrompt =
        """
        Ты исправляешь ошибки распознавания речи в транскрипте встречи на русском языке.

        Что делать:
        1. Исправляй слова, искажённые распознаванием, восстанавливая смысл по контексту диалога.
        2. Английские термины записывай латиницей в правильном написании
           (например: «эйпиай» → «API», «дед лайн» → «deadline», «кубернетес» → «Kubernetes»,
           «гитхаб» → «GitHub», «пул реквест» → «pull request»).
        3. Расставляй знаки препинания и заглавные буквы.
        4. Убирай слова-паразиты и оборванные повторы, если они мешают читать.

        Жёсткие правила:
        - НЕ придумывай факты и НЕ добавляй ничего, чего нет в реплике.
        - НЕ сокращай и НЕ пересказывай — объём текста должен сохраниться.
        - Сохраняй ЯЗЫК оригинала (русский), меняя латиницей только термины.
        - Отвечай СТРОГО в том же формате, что и вход: одна реплика на строку,
          каждая начинается с «[номер]» — тем же номером, что во входе.
          Никаких пояснений, заголовков и markdown.
        """;

    /// <summary>Партий крупнее этого размера модели уже не держат внимание на формате «[номер] текст».</summary>
    private const int MaxBatchTokens = 8192;

    /// <summary>
    /// Прогоняет реплики через LLM и возвращает исправленные (порядок и разметка спикеров сохраняются).
    /// При сбое разбора ответа исходные реплики возвращаются без изменений.
    /// </summary>
    /// <param name="progress">Сколько реплик уже обработано.</param>
    public static async Task<IReadOnlyList<TranscriptEntry>> CorrectAsync(
        OllamaClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        string glossary, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (entries.Count == 0) return entries;

        string header = BuildHeader(glossary);
        int window = await ollama.GetInputBudgetAsync(model, responseTokens: 0, ct);
        int batchTokens = BatchBudget(window, header);

        var corrected = new Dictionary<int, string>();
        int processed = 0;

        foreach (var (offset, batch) in SplitByBudget(entries, batchTokens))
        {
            var user = new StringBuilder(header).AppendLine("Исправь реплики:");
            for (int i = 0; i < batch.Count; i++)
                user.AppendLine($"[{offset + i}] {batch[i].Text}");

            // Ответ повторяет вход почти слово в слово — резервируем под него столько же с запасом.
            int reserve = OllamaClient.EstimateTokens(user.ToString()) * 5 / 4 + 256;
            var answer = await ollama.ChatAsync(model, SystemPrompt, user.ToString(), reserve, ct: ct);

            foreach (var (index, text) in ParseCorrections(answer))
                corrected[index] = text;

            processed += batch.Count;
            progress?.Report(processed);
        }

        if (corrected.Count == 0) return entries; // модель ответила не в формате — не портим транскрипт

        return entries
            .Select((e, i) => corrected.TryGetValue(i, out var text) ? e with { Text = text } : e)
            .ToList();
    }

    /// <summary>
    /// Разбирает ответ LLM вида «[i] текст» и подставляет исправленный текст в реплики.
    /// Реплики, для которых строка не нашлась, остаются исходными.
    /// </summary>
    public static IReadOnlyList<TranscriptEntry> ApplyCorrections(
        IReadOnlyList<TranscriptEntry> entries, string llmAnswer)
    {
        var corrected = ParseCorrections(llmAnswer);
        if (corrected.Count == 0) return entries; // модель ответила не в формате — не портим транскрипт

        return entries
            .Select((e, i) => corrected.TryGetValue(i, out var text) ? e with { Text = text } : e)
            .ToList();
    }

    private static Dictionary<int, string> ParseCorrections(string llmAnswer)
    {
        var corrected = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(llmAnswer, @"^\s*\[(\d+)\]\s*(.+?)\s*$", RegexOptions.Multiline))
        {
            if (int.TryParse(m.Groups[1].Value, out int idx) && m.Groups[2].Value.Length > 0)
                corrected[idx] = m.Groups[2].Value;
        }
        return corrected;
    }

    private static string BuildHeader(string glossary)
    {
        if (string.IsNullOrWhiteSpace(glossary)) return "";
        var sb = new StringBuilder();
        sb.AppendLine("Термины, имена и названия, которые встречаются в этой встрече " +
                      "(используй правильное написание из списка):");
        sb.AppendLine(glossary.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>Сколько токенов реплик класть в одну партию: вход и равный ему ответ должны влезть вместе.</summary>
    private static int BatchBudget(int window, string header)
    {
        if (window == int.MaxValue) return MaxBatchTokens; // облачная модель: окно огромно, но формат всё равно держится хуже на длинных партиях
        int service = OllamaClient.EstimateTokens(SystemPrompt) + OllamaClient.EstimateTokens(header) + 128;
        int usable = (window - service) * 2 / 5; // ~40 % окна на вход, остальное — ответ и запас
        return Math.Clamp(usable, 512, MaxBatchTokens);
    }

    private static List<(int Offset, List<TranscriptEntry> Items)> SplitByBudget(
        IReadOnlyList<TranscriptEntry> entries, int budget)
    {
        var result = new List<(int, List<TranscriptEntry>)>();
        var current = new List<TranscriptEntry>();
        int offset = 0, tokens = 0;

        for (int i = 0; i < entries.Count; i++)
        {
            int size = OllamaClient.EstimateTokens(entries[i].Text) + 8; // + «[номер] »
            if (current.Count > 0 && tokens + size > budget)
            {
                result.Add((offset, current));
                current = [];
                offset = i;
                tokens = 0;
            }
            current.Add(entries[i]);
            tokens += size;
        }

        if (current.Count > 0) result.Add((offset, current));
        return result;
    }

    /// <summary>
    /// Готовит hotwords для распознавателя: словарь GigaAM почти не знает латиницы,
    /// поэтому латинские термины из глоссария для смещения бесполезны — берём только
    /// кириллические слова (имена, названия, русские термины).
    /// </summary>
    public static List<string> ToHotwords(string glossary) =>
        Regex.Split(glossary ?? "", @"[,;\r\n]+")
            .Select(w => w.Trim())
            .Where(w => w.Length > 1 && Regex.IsMatch(w, @"^[а-яёА-ЯЁ\s-]+$"))
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();
}
