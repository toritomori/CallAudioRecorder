using System.Collections.Generic;
using System.Text;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>Промпт для формирования итогов встречи.</summary>
public static class SummaryPrompt
{
    /// <summary>Сколько токенов резервировать под сами итоги (структура из пяти разделов).</summary>
    public const int ResponseTokens = 3072;

    public const string System =
        """
        Ты — ассистент, составляющий итоги рабочей встречи по её транскрипту.
        Транскрипт размечен по спикерам: «Я» — владелец записи; «Собеседник 1», «Собеседник 2», …
        (или просто «Собеседник») — остальные участники, разделённые автоматически по голосу.
        Разметка может изредка путать похожие голоса.
        Распознавание речи могло внести мелкие ошибки — восстанавливай смысл по контексту.

        Ответь на русском языке в Markdown строго по структуре:

        ## Краткое резюме
        2–4 предложения о сути встречи.

        ## Ключевые темы и решения
        Маркированный список.

        ## Задачи
        Таблица: | Задача | Ответственный | Срок |
        Ответственный — «Я» или «Собеседник N» (или имя, если оно прозвучало). Если срок не назван — «—».

        ## Открытые вопросы
        Маркированный список (если нет — «Нет»).

        ## Следующие шаги
        Маркированный список.

        Не выдумывай фактов, которых нет в транскрипте.
        """;

    /// <summary>
    /// Промпт первого прохода для длинных встреч: транскрипт не влезает в окно модели целиком,
    /// поэтому он идёт по частям, а итоги строятся уже по конспектам частей.
    /// </summary>
    public const string PartSystem =
        """
        Ты конспектируешь ФРАГМЕНТ рабочей встречи по её транскрипту.
        Спикеры: «Я» — владелец записи; «Собеседник 1», «Собеседник 2», … — остальные участники.
        Распознавание речи могло внести мелкие ошибки — восстанавливай смысл по контексту.

        Верни на русском языке плотный конспект фрагмента в виде маркированного списка:
        обсуждавшиеся темы, принятые решения, задачи (с ответственным и сроком, если названы),
        открытые вопросы, важные числа, имена и названия.

        Правила:
        - Сохраняй, кто что сказал, если это важно для задач и решений.
        - Не выдумывай фактов, которых нет во фрагменте.
        - Никаких вступлений, заголовков и выводов о встрече в целом — только список.
        """;

    /// <summary>Сколько токенов резервировать под конспект одной части.</summary>
    public const int PartResponseTokens = 1536;

    public static string BuildUserMessage(IReadOnlyList<TranscriptEntry> entries)
    {
        var sb = new StringBuilder("Транскрипт звонка:\n\n");
        foreach (var e in entries)
            sb.AppendLine(FormatEntry(e));
        return sb.ToString();
    }

    /// <summary>Сообщение первого прохода: один фрагмент длинной встречи.</summary>
    public static string BuildPartMessage(IReadOnlyList<TranscriptEntry> entries, int index, int total)
    {
        var sb = new StringBuilder($"Фрагмент {index} из {total} (транскрипт звонка):\n\n");
        foreach (var e in entries)
            sb.AppendLine(FormatEntry(e));
        return sb.ToString();
    }

    /// <summary>Сообщение второго прохода: итоги строятся по конспектам частей, а не по транскрипту.</summary>
    public static string BuildFromParts(IReadOnlyList<string> parts)
    {
        var sb = new StringBuilder(
            "Встреча была длинной, поэтому ниже — конспекты её частей в хронологическом порядке. " +
            "Составь итоги всей встречи целиком по этим конспектам.\n\n");
        for (int i = 0; i < parts.Count; i++)
            sb.AppendLine($"### Часть {i + 1} из {parts.Count}").AppendLine(parts[i].Trim()).AppendLine();
        return sb.ToString();
    }

    public static string FormatEntry(TranscriptEntry e) => $"[{e.TimeLabel}] {e.Speaker}: {e.Text}";
}
