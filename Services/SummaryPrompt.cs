using System.Collections.Generic;
using System.Text;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>
/// Промпт для формирования итогов встречи. Итоги пишутся на языке записи:
/// русский транскрипт — русские итоги, английский — английские.
/// </summary>
public static class SummaryPrompt
{
    /// <summary>Сколько токенов резервировать под сами итоги (структура из пяти разделов).</summary>
    public const int ResponseTokens = 3072;

    /// <summary>Сколько токенов резервировать под конспект одной части.</summary>
    public const int PartResponseTokens = 1536;

    private const string SystemRu =
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

    private const string SystemEn =
        """
        You are an assistant that writes up the notes of a work meeting from its transcript.
        The transcript is labelled by speaker: "Me" is the owner of the recording; "Speaker 1",
        "Speaker 2", … (or just "Speaker") are the other participants, separated automatically
        by voice. The labelling occasionally confuses similar voices.
        Speech recognition may have introduced small errors — recover the meaning from context.

        Answer in English, in Markdown, strictly in this structure:

        ## Summary
        2–4 sentences on what the meeting was about.

        ## Key topics and decisions
        A bulleted list.

        ## Tasks
        A table: | Task | Owner | Due |
        The owner is "Me" or "Speaker N" (or a name, if one was said). If no due date was named, use "—".

        ## Open questions
        A bulleted list (if there are none — "None").

        ## Next steps
        A bulleted list.

        Do not invent facts that are not in the transcript.
        """;

    /// <summary>
    /// Промпт первого прохода для длинных встреч: транскрипт не влезает в окно модели целиком,
    /// поэтому он идёт по частям, а итоги строятся уже по конспектам частей.
    /// </summary>
    private const string PartSystemRu =
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

    private const string PartSystemEn =
        """
        You are summarising a FRAGMENT of a work meeting from its transcript.
        Speakers: "Me" is the owner of the recording; "Speaker 1", "Speaker 2", … are the others.
        Speech recognition may have introduced small errors — recover the meaning from context.

        Return a dense summary of the fragment in English as a bulleted list:
        the topics discussed, decisions made, tasks (with owner and due date if named),
        open questions, important numbers, names and titles.

        Rules:
        - Keep track of who said what where it matters for tasks and decisions.
        - Do not invent facts that are not in the fragment.
        - No preamble, no headings, no conclusions about the meeting as a whole — just the list.
        """;

    private static bool IsEnglish(LanguageProfile? language) =>
        language?.Language == TranscriptionLanguage.English;

    public static string SystemFor(LanguageProfile? language) => IsEnglish(language) ? SystemEn : SystemRu;

    public static string PartSystemFor(LanguageProfile? language) =>
        IsEnglish(language) ? PartSystemEn : PartSystemRu;

    /// <summary>
    /// Шапка с глоссарием. В отличие от <see cref="TranscriptCorrector"/>, который правит сам
    /// транскрипт, здесь модель читает искажённый текст и должна узнать термин по звучанию:
    /// редкие названия распознавание разносит на десяток написаний («Interstation», «Intersticial»,
    /// «интерстишул» — это одно слово), и без списка итоги наследуют этот разнобой.
    /// </summary>
    public static string BuildGlossaryBlock(string? glossary, LanguageProfile? language = null)
    {
        if (string.IsNullOrWhiteSpace(glossary)) return "";
        var sb = new StringBuilder();
        sb.AppendLine(IsEnglish(language)
            ? "Terms, names and titles that come up in this meeting. Recognition may have mangled " +
              "them in the transcript — recognise them by how they sound and use the spelling " +
              "from this list in the notes:"
            : "Термины, имена и названия, которые встречаются в этой встрече. В транскрипте они " +
              "могли быть искажены распознаванием — узнавай их по звучанию и пиши в итогах " +
              "в правильном написании из списка:");
        sb.AppendLine(glossary.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    public static string BuildUserMessage(IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile? language = null, string? glossary = null)
    {
        var sb = new StringBuilder(BuildGlossaryBlock(glossary, language));
        sb.AppendLine(IsEnglish(language) ? "Call transcript:" : "Транскрипт звонка:").AppendLine();
        foreach (var e in entries)
            sb.AppendLine(FormatEntry(e));
        return sb.ToString();
    }

    /// <summary>Сообщение первого прохода: один фрагмент длинной встречи.</summary>
    public static string BuildPartMessage(IReadOnlyList<TranscriptEntry> entries, int index, int total,
        LanguageProfile? language = null, string? glossary = null)
    {
        var sb = new StringBuilder(BuildGlossaryBlock(glossary, language));
        sb.AppendLine(IsEnglish(language)
            ? $"Fragment {index} of {total} (call transcript):"
            : $"Фрагмент {index} из {total} (транскрипт звонка):").AppendLine();
        foreach (var e in entries)
            sb.AppendLine(FormatEntry(e));
        return sb.ToString();
    }

    /// <summary>Сообщение второго прохода: итоги строятся по конспектам частей, а не по транскрипту.</summary>
    public static string BuildFromParts(IReadOnlyList<string> parts, LanguageProfile? language = null,
        string? glossary = null)
    {
        bool english = IsEnglish(language);
        var sb = new StringBuilder(BuildGlossaryBlock(glossary, language));
        sb.Append(english
            ? "The meeting was long, so below are summaries of its parts in chronological order. " +
              "Write up the notes for the meeting as a whole from these summaries.\n\n"
            : "Встреча была длинной, поэтому ниже — конспекты её частей в хронологическом порядке. " +
              "Составь итоги всей встречи целиком по этим конспектам.\n\n");
        for (int i = 0; i < parts.Count; i++)
            sb.AppendLine(english ? $"### Part {i + 1} of {parts.Count}" : $"### Часть {i + 1} из {parts.Count}")
              .AppendLine(parts[i].Trim()).AppendLine();
        return sb.ToString();
    }

    /// <summary>Заголовки разделов итогов — по ним самотест проверяет, что ответ не оборвался.</summary>
    public static string[] SectionsFor(LanguageProfile? language) => IsEnglish(language)
        ? ["## Summary", "## Key topics and decisions", "## Tasks", "## Open questions", "## Next steps"]
        : ["## Краткое резюме", "## Ключевые темы и решения", "## Задачи", "## Открытые вопросы", "## Следующие шаги"];

    public static string FormatEntry(TranscriptEntry e) => $"[{e.TimeLabel}] {e.Speaker}: {e.Text}";
}
