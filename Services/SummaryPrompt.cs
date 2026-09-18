using System.Collections.Generic;
using System.Text;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>
/// Промпт для формирования итогов встречи. Итоги пишутся на языке записи:
/// русский транскрипт — русские итоги, английский — английские.
///
/// Формат ответа — только маркированные списки, без таблиц: итоги копируют в Slack, а он
/// Markdown-таблицы не отображает — задачи приезжали туда строкой из палок и дефисов.
/// </summary>
public static class SummaryPrompt
{
    /// <summary>Сколько токенов резервировать под сами итоги (структура из пяти разделов).</summary>
    public const int ResponseTokens = 4096;

    /// <summary>
    /// Сколько токенов резервировать под конспект одной части — это и потолок его длины.
    /// 2048 было впритык: часть — до ~35 минут разговора, а от конспекта требуется полнота.
    /// Если конспект всё же обрывается, SummaryComposer делит часть пополам.
    /// </summary>
    public const int PartResponseTokens = 4096;

    private const string SystemRu =
        """
        Ты — ассистент, составляющий итоги рабочей встречи по её транскрипту.

        Транскрипт идёт строками вида «[00:12:31] Собеседник 2: текст реплики».
        «Я» — владелец записи; «Собеседник 1», «Собеседник 2», … (или просто «Собеседник») —
        остальные участники, разделённые автоматически по голосу; вместо метки может стоять имя.
        Разметка изредка путает похожие голоса, а распознавание речи могло исказить слова —
        восстанавливай смысл по контексту, но фактов не додумывай.

        Порядок работы:
        1. Пройди транскрипт от первой реплики до последней. Решения и задачи часто проговаривают
           в самом конце и скороговоркой — конец важен не меньше начала.
        2. Отметь всё, что подпадает под разделы ниже, в порядке появления.
        3. Только после этого пиши ответ. Краткость — не повод выбросить договорённость.

        Обязательно попадает в итоги:
        - договорённости и решения, в том числе брошенные вскользь («ок, тогда делаем так»);
        - любое обещание что-то сделать («посмотрю», «скину», «уточню») — это задача;
        - числа, даты, сроки, версии, суммы, метрики, названия и имена — переноси дословно;
        - причины решения и прозвучавшие возражения;
        - проблемы и риски, даже если их не решили.

        Ответь на русском языке в Markdown строго по структуре ниже: без вступлений и заключений,
        без таблиц и блоков кода (итоги копируются в Slack, таблицы там не отображаются).

        ## Краткое резюме
        2–4 предложения: зачем собрались и о чём договорились.

        ## Ключевые темы и решения
        Маркированный список, по одному пункту на тему, в порядке обсуждения. Образец формы
        (содержание бери только из транскрипта):
        - **Цены на подписку** — поднимаем на 10 % с ноября, конкуренты уже подняли.
        - **Логотип** — решения нет, вернулись к обсуждению на следующей встрече.

        ## Задачи
        Маркированный список, по одному пункту на задачу, без таблицы. Каждая строка — ровно
        три части через « — »: что сделать, «исполнитель: …», «срок: …». Образец формы:
        - Согласовать договор с подрядчиком — исполнитель: Собеседник 2 — срок: 15 октября
        - Собрать отзывы пользователей — исполнитель: Я — срок: не назван
        «срок:» пишется в КАЖДОЙ строке; если срок не прозвучал — «срок: не назван».
        Одна задача — один пункт, даже если её взяли на себя двое: не дублируй её для каждого.
        Исполнитель — ровно один: «Я», «Собеседник N» или имя; если его не назвали или вызвались
        несколько — «исполнитель: не определён», а не список участников. Если задач нет — «Нет».

        ## Открытые вопросы
        Маркированный список: вопросы без ответа и решения, отложенные на потом.
        Если таких нет — «Нет».

        ## Следующие шаги
        Маркированный список: что происходит после встречи — следующие созвоны, чего ждут от
        других команд, контрольные точки. Задачи целиком сюда не переписывай.

        Правила:
        - Каждый пункт — законченная мысль, понятная без транскрипта: кто, что, когда.
        - Никаких «участники обсудили ряд вопросов» — пиши, какие именно.
        - Не выдумывай того, чего в транскрипте нет.
        - Не пиши ничего, кроме пяти разделов выше.
        """;

    private const string SystemEn =
        """
        You are an assistant that writes up the notes of a work meeting from its transcript.

        The transcript comes as lines like "[00:12:31] Speaker 2: what was said".
        "Me" is the owner of the recording; "Speaker 1", "Speaker 2", … (or just "Speaker") are
        the other participants, separated automatically by voice; a name may stand in for a label.
        The labelling occasionally confuses similar voices and speech recognition may have mangled
        words — recover the meaning from context, but do not invent facts.

        How to work:
        1. Go through the transcript from the first line to the last. Decisions and tasks are often
           rattled off right at the end — the ending matters as much as the opening.
        2. Note everything that belongs in the sections below, in the order it came up.
        3. Only then write the answer. Being brief is no reason to drop an agreement.

        Always include:
        - agreements and decisions, including ones dropped in passing ("ok, let's do that then");
        - any promise to do something ("I'll look at it", "I'll send it over") — that is a task;
        - numbers, dates, deadlines, versions, amounts, metrics, names and titles — verbatim;
        - the reasons behind a decision and any objections raised;
        - problems and risks, even the unresolved ones.

        Answer in English, in Markdown, strictly in the structure below: no preamble, no closing
        remarks, no tables and no code blocks (the notes get pasted into Slack, which does not
        render tables).

        ## Summary
        2–4 sentences: why the meeting happened and what was agreed.

        ## Key topics and decisions
        A bulleted list, one bullet per topic, in the order discussed. A sample of the form
        (take the content from the transcript only):
        - **Subscription pricing** — going up by 10% from November, competitors already did.
        - **Logo** — nothing decided, taken to the next meeting.

        ## Tasks
        A bulleted list, one bullet per task, no table. Every line has exactly three parts
        joined by " — ": what to do, "owner: …", "due: …". A sample of the form:
        - Sign off the contractor agreement — owner: Speaker 2 — due: 15 October
        - Collect user feedback — owner: Me — due: not named
        Write "due:" on EVERY line; if no date was given — "due: not named".
        One task, one bullet, even if two people took it on: do not repeat it for each of them.
        Exactly one owner: "Me", "Speaker N" or a name; if none was named or several volunteered,
        write "owner: unassigned" rather than a list of participants. If there are none — "None".

        ## Open questions
        A bulleted list: questions left unanswered and decisions postponed.
        If there are none — "None".

        ## Next steps
        A bulleted list: what happens after the meeting — follow-up calls, what is expected from
        other teams, checkpoints. Do not restate the tasks here in full.

        Rules:
        - Every bullet is a complete thought that stands without the transcript: who, what, when.
        - No "the participants discussed a number of issues" — say which ones.
        - Do not invent anything that is not in the transcript.
        - Write nothing beyond the five sections above.
        """;

    /// <summary>
    /// Промпт первого прохода для длинных встреч: транскрипт не влезает в окно модели целиком,
    /// поэтому он идёт по частям, а итоги строятся уже по конспектам частей. Полнота здесь важнее
    /// краткости: что не попало в конспект, во второй проход уже не попадёт.
    /// </summary>
    private const string PartSystemRu =
        """
        Ты конспектируешь ФРАГМЕНТ рабочей встречи по её транскрипту. Это черновик для итогов всей
        встречи: что не попало в конспект — потеряно навсегда, поэтому важна полнота, а не краткость.

        Спикеры: «Я» — владелец записи; «Собеседник 1», «Собеседник 2», … — остальные участники
        (вместо метки может стоять имя). Распознавание могло исказить слова — восстанавливай смысл
        по контексту, но фактов не додумывай.

        Пройди фрагмент от первой реплики до последней и верни на русском языке маркированный
        список. Каждый пункт начинай с пометки:
        - Тема: что обсуждали и какие аргументы прозвучали.
        - Решение: о чём договорились и кто это сказал.
        - Задача: что сделать — исполнитель — срок (если названы).
        - Вопрос: что осталось без ответа или отложено на потом.
        - Факт: числа, даты, версии, метрики, суммы, названия и имена — дословно.

        Правила:
        - Не обобщай до бессмыслицы: «обсудили сроки» бесполезно, нужны сами сроки.
        - Сохраняй, кто что сказал, если это важно для задач и решений.
        - Не выдумывай фактов, которых нет во фрагменте.
        - Никаких вступлений, заголовков и выводов о встрече в целом — только список.
        """;

    private const string PartSystemEn =
        """
        You are summarising a FRAGMENT of a work meeting from its transcript. This is raw material
        for the notes on the whole meeting: whatever misses the summary is lost for good, so
        completeness matters more than brevity.

        Speakers: "Me" is the owner of the recording; "Speaker 1", "Speaker 2", … are the others
        (a name may stand in for a label). Recognition may have mangled words — recover the meaning
        from context, but do not invent facts.

        Go through the fragment from the first line to the last and return a bulleted list in
        English. Start every bullet with a tag:
        - Topic: what was discussed and which arguments were made.
        - Decision: what was agreed and who said it.
        - Task: what to do — owner — due date (if named).
        - Question: what was left unanswered or postponed.
        - Fact: numbers, dates, versions, metrics, amounts, names and titles — verbatim.

        Rules:
        - Do not generalise into nothing: "discussed the deadlines" is useless, the dates are not.
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
              "They are the only record of the meeting you have: no decision, task, deadline or " +
              "figure mentioned in them may be dropped from the notes. Write up the notes for the " +
              "meeting as a whole from these summaries.\n\n"
            : "Встреча была длинной, поэтому ниже — конспекты её частей в хронологическом порядке. " +
              "Другого источника у тебя нет: ни одно решение, задача, срок или число из конспектов " +
              "не должно потеряться в итогах. Составь итоги всей встречи целиком по этим " +
              "конспектам.\n\n");
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
