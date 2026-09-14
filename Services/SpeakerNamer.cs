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
/// Замена меток «Собеседник N» именами, которые прозвучали в разговоре.
///
/// Зачем: диаризация различает голоса, но не знает, кого как зовут, — и в списке задач
/// исполнителем оказывается «Собеседник 12». При этом на планёрках люди постоянно
/// обращаются друг к другу по имени и передают слово, так что имя обычно стоит прямо
/// в соседней реплике.
///
/// Как: у модели спрашивается ТОЛЬКО список имён, звучавших во встрече, а кто из них кто —
/// решается детерминированно, по правилу «назвал имя — говорит следующий». Привязку у модели
/// спрашивать бесполезно: она путает направление обращения (на реальной планёрке всё смещалось
/// на одного — «Дальше, Дима» отдавало имя Дима тому, кто это произнёс), а 9B-модель на такой
/// вопрос вместо формата «метка = имя» выдаёт поток рассуждений, из которого не разобрать ничего.
///
/// Не уверены — не переименовываем: метка «Собеседник N» честнее чужого имени в задачах.
/// </summary>
public static class SpeakerNamer
{
    /// <summary>Сколько токенов резервировать под ответ: список имён через запятую.</summary>
    private const int ResponseTokens = 256;

    /// <summary>
    /// Слов от начала и от конца реплики, где имя вообще может быть обращением: в середине
    /// получасового спора имена звучат про третьих лиц, и голос оттуда только шумит.
    /// </summary>
    private const int HeadWords = 15, TailWords = 30;

    /// <summary>
    /// Вес голоса. Уверенным считается только явная передача слова («Дальше, Дима.», «Ира.»,
    /// «Передаю слово давай Людмиле») — без неё имя не назначается вовсе: на реальной планёрке
    /// пара упоминаний в третьем лице сделала «Собеседника 6» Андреем, которого там не было.
    /// </summary>
    private const int StrongWeight = 4, WeakWeight = 1;

    /// <summary>
    /// Слова, рядом с которыми имя — почти наверняка передача слова, а не разговор о человеке.
    /// </summary>
    private static readonly HashSet<string> HandoverMarkers = new(StringComparer.Ordinal)
    {
        "передаю", "передам", "слово", "дальше", "давай", "давайте", "можно", "очередь",
        "расскажи", "рассказывай", "слушаем", "начинай", "тебя", "тебе", "твоя",
        "over", "next", "ahead", "turn", "hand", "tell", "floor", "you", "your",
    };

    private const string NamesSystemRu =
        """
        По транскрипту встречи перечисли ИМЕНА её участников — те, что звучат в самом разговоре:
        к людям обращаются по имени, передают слово, представляются.

        Ответь одной строкой: имена через запятую, каждое в именительном падеже и с заглавной
        буквы («Артёму» → «Артём»). Только имена людей — не названия проектов, команд и задач.
        Если имён не прозвучало — ответь «?». Никаких пояснений.
        """;

    private const string NamesSystemEn =
        """
        From the meeting transcript, list the NAMES of its participants — the ones actually said
        in the conversation: people address each other by name, hand over the floor, introduce
        themselves.

        Answer with a single line: names separated by commas, each capitalised and in its base
        form. People's names only — no project, team or task names. If no names were said,
        answer "?". No explanations.
        """;

    /// <summary>
    /// Определяет имена и возвращает реплики с заменёнными метками. Если ничего надёжного
    /// не нашлось, возвращает исходные реплики.
    /// </summary>
    public static async Task<IReadOnlyList<TranscriptEntry>> ApplyNamesAsync(
        IChatClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile? language = null, string? ownerName = null, CancellationToken ct = default)
    {
        var names = await DetectAsync(ollama, model, entries, language, ownerName: ownerName, ct: ct).ConfigureAwait(false);
        if (names.Count == 0) return entries;

        return entries
            .Select(e => names.TryGetValue(e.Speaker, out var name) ? e with { Speaker = name } : e)
            .ToList();
    }

    /// <summary>Карта «метка спикера → имя». Пустая, если ничего не удалось определить.</summary>
    /// <param name="trace">Куда сложить ответ модели, кандидатов и голоса — для самотеста.</param>
    public static async Task<IReadOnlyDictionary<string, string>> DetectAsync(
        IChatClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile? language = null, ICollection<string>? trace = null, string? ownerName = null,
        CancellationToken ct = default)
    {
        var labels = entries.Select(e => e.Speaker).Where(s => !Languages.IsMeLabel(s))
            .Distinct().ToHashSet();
        if (labels.Count == 0) return new Dictionary<string, string>();

        bool english = language?.Language == TranscriptionLanguage.English;

        int budget = await ollama.GetInputBudgetAsync(model, ResponseTokens, ct).ConfigureAwait(false);
        var system = english ? NamesSystemEn : NamesSystemRu;
        var user = BuildMessage(entries, budget - OllamaClient.EstimateTokens(system) - 256, english);
        // Температура 0: на 0.3 список имён менялся от прогона к прогону.
        var answer = await ollama.ChatAsync(model, system, user, ResponseTokens, ct: ct, temperature: 0).ConfigureAwait(false);
        trace?.Add($"ответ модели:{Environment.NewLine}{answer.Trim()}");

        var candidates = ExtractNames(answer, entries, language ?? Languages.Russian, trace);
        trace?.Add($"кандидаты: {(candidates.Count == 0 ? "—" : string.Join(", ", candidates))}");

        // Имя владельца записи собеседнику доставаться не должно: на планёрках к нему
        // обращаются чаще всех, и любое такое обращение — голос за следующего говорящего.
        var owner = string.IsNullOrWhiteSpace(ownerName)
            ? await AskOwnerAsync(ollama, model, user, english, ct).ConfigureAwait(false)
            : ownerName.Trim();
        if (owner is not null) candidates.RemoveWhere(n => Same(n, owner));
        trace?.Add($"владелец записи: {owner ?? "не определён"}" +
                   (string.IsNullOrWhiteSpace(ownerName) ? " (определён моделью)" : " (из настроек)"));

        if (candidates.Count == 0) return new Dictionary<string, string>();

        var votes = CollectVotes(entries, candidates, labels);
        foreach (var (label, vote) in votes.OrderBy(p => p.Key, StringComparer.Ordinal))
            trace?.Add($"голоса «{label}»: " + string.Join(", ", vote.Weight
                .OrderByDescending(p => p.Value)
                .Select(p => $"{p.Key}={p.Value}{(vote.Confirmed.Contains(p.Key) ? " (передача слова)" : "")}")));

        return Resolve(votes, trace);
    }

    /// <summary>
    /// Имена из ответа модели, оставшиеся после проверки по самому транскрипту. Модель может
    /// вместо списка начать рассуждать вслух — поэтому берутся все слова с заглавной буквы,
    /// а отсев делает транскрипт: имя должно в нём встречаться, причём хотя бы раз с заглавной
    /// буквы в СЕРЕДИНЕ предложения. Так отсеиваются и «Значит» из рассуждений модели,
    /// и выдуманные участники, которых в записи не было.
    ///
    /// Этого мало, когда модель зацикливается и копирует фразы транскрипта: на записи от 20.08
    /// «Вот» и «Там» прошли проверку, потому что распознавание пишет их отдельным предложением
    /// («Вот.»), и «Вот» отобрало метку у Вячеслава. Поэтому ещё два правила:
    /// — слово, которое в транскрипте чаще написано со строчной, чем как имя, — не имя
    ///   («Вот» 23 раза как имя и 111 со строчной; у настоящих имён строчных написаний нет).
    ///   Сравниваются падежные формы (<see cref="IsForm"/>): «Поке» из того же ответа прошло
    ///   бы по одному слову «поке» (1 как имя, 4 со строчной — вроде бы имя), но его форма
    ///   «пока» встречается со строчной 20 раз против 3 — и «Поке» отобрало метку у Вячеслава;
    /// — имя в чужом алфавите — не имя: латиница в русском транскрипте берётся только из
    ///   канонизации терминов по глоссарию (Poki, Google, Crazy Games), распознавание её не пишет.
    /// Настоящее имя, отсеянное по ошибке, оставит метку «Собеседник N», а пропущенное
    /// служебное слово станет чужим «именем» в задачах, — поэтому правила строже, а не мягче.
    /// На пяти транскриптах корпуса правила не задели ни одного настоящего имени.
    /// </summary>
    private static HashSet<string> ExtractNames(string answer, IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile language, ICollection<string>? trace)
    {
        var (asName, lower) = CountWritings(entries);
        bool english = language.Language == TranscriptionLanguage.English;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejected = new List<string>();

        foreach (Match m in Regex.Matches(answer, @"\p{Lu}\p{Ll}{2,}"))
        {
            var name = m.Value;
            if (Languages.IsMeLabel(name) || !seen.Add(name)) continue;
            // Сверяем с точностью до падежа: в транскрипте имя чаще стоит в косвенном
            // («Передаю слово Артёму»), а модель называет его в именительном.
            if (!asName.Keys.Any(word => Same(word, name))) continue;

            if (!language.HotwordFilter(name))
            {
                rejected.Add($"{name} (не в алфавите языка записи)");
                continue;
            }
            int formsAsName = asName.Where(p => IsForm(p.Key, name, english)).Sum(p => p.Value);
            int formsLower = lower.Where(p => IsForm(p.Key, name, english)).Sum(p => p.Value);
            if (formsLower > formsAsName)
            {
                rejected.Add($"{name} (со строчной {formsLower} раз, как имя {formsAsName})");
                continue;
            }
            result.Add(name);
        }

        if (rejected.Count > 0) trace?.Add($"отсеяны: {string.Join(", ", rejected)}");
        return result;
    }

    /// <summary>Окончания, которые падеж прибавляет к основе имени: «Вер|у», «Артём|у», «Алексе|ю».</summary>
    private static readonly HashSet<string> Endings = new(StringComparer.Ordinal)
        { "", "а", "я", "ы", "и", "е", "у", "ю", "й", "ь", "ой", "ей", "ою", "ею", "ом", "ем", "ам", "ям", "ах", "ях" };

    /// <summary>
    /// Падежная форма имени: «Вере» и «Веру» — формы «Вера», а «верно» и «версия» — нет.
    /// Для сравнения «как имя / со строчной» основа из <see cref="Same"/> слишком широка:
    /// основа «вер» накрыла бы «верно», и настоящая Вера отсеялась бы. В английском имена
    /// не склоняются — там сравнивается слово целиком.
    /// </summary>
    private static bool IsForm(string word, string name, bool english)
    {
        var w = Normalize(word);
        var n = Normalize(name);
        if (w == n) return true;
        if (english || n.Length < 3) return false;
        var stem = "аяеоиыуюйь".Contains(n[^1], StringComparison.Ordinal) ? n[..^1] : n;
        return w.StartsWith(stem, StringComparison.Ordinal) && Endings.Contains(w[stem.Length..]);
    }

    /// <summary>
    /// Как слова написаны в транскрипте. «Как имя» — с заглавной буквы не в начале предложения
    /// («созвониться с Людмилой») либо целым предложением из одного слова: так распознавание
    /// оформляет передачу слова в конце реплики («…в пятницу был два. Ира.»). Строчные
    /// написания хранятся нормализованными — сравниваются по падежным формам.
    /// </summary>
    private static (Dictionary<string, int> AsName, Dictionary<string, int> Lower) CountWritings(
        IReadOnlyList<TranscriptEntry> entries)
    {
        var asName = new Dictionary<string, int>(StringComparer.Ordinal);
        var lower = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            foreach (var sentence in Regex.Split(entry.Text, @"(?<=[.!?…])\s+"))
            {
                var words = Regex.Matches(sentence, @"\p{L}[\p{L}\p{Nd}]*")
                    .Select(m => m.Value).ToArray();
                for (int i = 0; i < words.Length; i++)
                {
                    if (char.IsLower(words[i][0]))
                    {
                        var key = Normalize(words[i]);
                        lower[key] = lower.GetValueOrDefault(key) + 1;
                    }
                    else if (i > 0 || words.Length == 1)
                    {
                        asName[words[i]] = asName.GetValueOrDefault(words[i]) + 1;
                    }
                }
            }
        }
        return (asName, lower);
    }

    /// <summary>
    /// Голоса «метка = имя». Обращение и передача слова указывают на того, кто говорит
    /// СЛЕДУЮЩИМ, а не на говорящего: «Дальше, Дима» произносит не Дима. Это и есть место,
    /// где раньше разметка уезжала на одного человека.
    /// </summary>
    private static Dictionary<string, Vote> CollectVotes(
        IReadOnlyList<TranscriptEntry> entries, HashSet<string> names, HashSet<string> labels)
    {
        var votes = new Dictionary<string, Vote>(StringComparer.Ordinal);

        void Add(string label, string name, bool strong)
        {
            if (!labels.Contains(label)) return;
            var vote = votes.TryGetValue(label, out var existing) ? existing : votes[label] = new Vote();
            vote.Weight[name] = vote.Weight.GetValueOrDefault(name) + (strong ? StrongWeight : WeakWeight);
            if (strong) vote.Confirmed.Add(name);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var next = NextSpeaker(entries, i);
            foreach (var (name, strong, self) in Mentions(entries[i].Text, names))
            {
                if (self) Add(entries[i].Speaker, name, strong);
                else if (next is not null) Add(next, name, strong);
            }
        }
        return votes;
    }

    /// <summary>Голоса за одну метку: вес по именам и те имена, за которыми есть явная передача слова.</summary>
    private sealed class Vote
    {
        public Dictionary<string, int> Weight { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Confirmed { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Упоминания имён в реплике: само имя, уверенный ли это голос и относится ли он
    /// к говорящему (самопредставление) или к следующему (обращение, передача слова).
    ///
    /// Уверенным упоминание делает форма, а не место: имя отдельным предложением («Ира.»),
    /// имя с запятой в начале предложения («Дальше, Дима.») или короткое предложение
    /// с маркером передачи («Передаю слово давай Людмиле»). Всё прочее — упоминание
    /// в третьем лице («Вижу Артёма контент»), и одного его мало, чтобы переименовать метку.
    /// </summary>
    private static IEnumerable<(string Name, bool Strong, bool Self)> Mentions(
        string text, HashSet<string> names)
    {
        var words = Words(text);
        if (words.Length == 0) yield break;

        int position = 0;
        foreach (var sentence in Regex.Split(text, @"(?<=[.!?…])\s+"))
        {
            var inSentence = Tokens(sentence);
            if (inSentence.Length == 0) continue;

            bool marker = inSentence.Any(t => HandoverMarkers.Contains(t.Word));
            for (int i = 0; i < inSentence.Length; i++)
            {
                int atEntry = position + i;
                bool atEdge = atEntry < HeadWords || words.Length - 1 - atEntry < TailWords;

                foreach (var name in names)
                {
                    if (!Same(inSentence[i].Word, name)) continue;

                    if (i > 0 && SelfIntro.Contains(inSentence[i - 1].Word))
                    {
                        yield return (name, true, true);
                        continue;
                    }
                    if (!atEdge) continue;

                    bool strong = inSentence.Length == 1
                               || (i < 3 && inSentence[i].Comma)
                               || (marker && inSentence.Length <= 12);
                    yield return (name, strong, false);
                }
            }
            position += inSentence.Length;
        }
    }

    /// <summary>Слова, после которых имя называет самого говорящего: «я Марина», «меня зовут Марина».</summary>
    private static readonly HashSet<string> SelfIntro = new(StringComparer.Ordinal)
        { "я", "зовут", "это", "i", "am", "this", "is" };

    /// <summary>Слова предложения вместе с признаком «после слова стоит запятая».</summary>
    private static (string Word, bool Comma)[] Tokens(string sentence) =>
        Regex.Matches(sentence.ToLowerInvariant().Replace('ё', 'е'), @"[\p{L}\p{Nd}]+\s*,?")
            .Select(m => (Word: Regex.Replace(m.Value, @"[^\p{L}\p{Nd}]", ""),
                          Comma: m.Value.TrimEnd().EndsWith(',')))
            .Where(t => t.Word.Length > 0)
            .ToArray();

    /// <summary>Кто заговорит после реплики <paramref name="i"/>, или null, если она последняя.</summary>
    private static string? NextSpeaker(IReadOnlyList<TranscriptEntry> entries, int i)
    {
        for (int j = i + 1; j < entries.Count; j++)
            if (!string.Equals(entries[j].Speaker, entries[i].Speaker, StringComparison.Ordinal))
                return entries[j].Speaker;
        return null;
    }

    /// <summary>
    /// Кому какое имя достаётся. Отказ вместо догадки: имя не назначается, если за ним нет
    /// ни одной явной передачи слова, если у метки два равных кандидата или если на одно имя
    /// одинаково претендуют две метки, — «Собеседник N» в задачах честнее чужого имени.
    /// </summary>
    private static Dictionary<string, string> Resolve(
        Dictionary<string, Vote> votes, ICollection<string>? trace)
    {
        var claims = new List<(string Label, string Name, int Weight)>();
        foreach (var (label, vote) in votes)
        {
            var ranked = vote.Weight.OrderByDescending(p => p.Value).ToList();
            if (ranked.Count == 0) continue;
            if (!vote.Confirmed.Contains(ranked[0].Key))
            {
                trace?.Add($"«{label}» оставлен как есть: имя «{ranked[0].Key}» только упоминалось " +
                           "рядом, слово ему никто не передавал");
                continue;
            }
            if (ranked.Count > 1 && ranked[0].Value == ranked[1].Value)
            {
                trace?.Add($"«{label}» оставлен как есть: голоса поровну " +
                           $"({ranked[0].Key} и {ranked[1].Key} по {ranked[0].Value})");
                continue;
            }
            claims.Add((label, ranked[0].Key, ranked[0].Value));
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in claims.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ranked = group.OrderByDescending(c => c.Weight).ToList();
            if (ranked.Count > 1 && ranked[0].Weight == ranked[1].Weight)
            {
                trace?.Add($"имя «{group.Key}» не назначено: на него одинаково претендуют " +
                           string.Join(" и ", ranked.Take(2).Select(c => $"«{c.Label}»")));
                continue;
            }
            result[ranked[0].Label] = ranked[0].Name;
            foreach (var loser in ranked.Skip(1))
                trace?.Add($"«{loser.Label}» оставлен как есть: имя «{group.Key}» ушло " +
                           $"к «{ranked[0].Label}» ({ranked[0].Weight} против {loser.Weight})");
        }
        return result;
    }

    /// <summary>Имя владельца записи (метка «Я»), или null, если модель его не назвала.</summary>
    private static async Task<string?> AskOwnerAsync(
        IChatClient ollama, string model, string transcript, bool english, CancellationToken ct)
    {
        const string systemRu =
            """
            По транскрипту встречи определи, как зовут ВЛАДЕЛЬЦА записи — участника с меткой «Я».
            К нему обращаются по имени остальные участники.
            Ответь одним словом: именем в именительном падеже или «?», если имя не прозвучало.
            Никаких пояснений.
            """;
        const string systemEn =
            """
            From the meeting transcript, work out the name of the recording's OWNER —
            the participant labelled "Me". The others address them by name.
            Answer with a single word: the name, or "?" if it was never said. No explanations.
            """;

        var answer = await ollama.ChatAsync(model, english ? systemEn : systemRu, transcript,
            responseTokens: 32, ct: ct, temperature: 0).ConfigureAwait(false);
        var name = answer.Trim().Trim('«', '»', '"', '.', ',');
        return name.Length >= 2 && name != "?" && !name.Contains(' ') ? name : null;
    }

    /// <summary>Слова реплики в сравнимом виде: нижний регистр, без знаков препинания.</summary>
    private static string[] Words(string text) =>
        Regex.Split(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+")
            .Where(w => w.Length > 0).ToArray();

    /// <summary>
    /// Одно и то же имя с точностью до падежа: «Людмиле» — это «Людмила». Сверяем по основе,
    /// но короткие имена («Ира», «Дима») — целиком: основа из двух букв цепляет пол-словаря.
    /// </summary>
    private static bool Same(string word, string name)
    {
        var w = Normalize(word);
        var n = Normalize(name);
        if (w.Length == 0 || n.Length == 0) return false;
        if (w == n) return true;
        if (n.Length < 4) return false;
        var stem = n[..^1];
        return w.Length >= stem.Length && w.StartsWith(stem, StringComparison.Ordinal);
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+", "");

    /// <summary>
    /// Сообщение для модели: реплики с метками, сколько влезает в окно. Имена чаще всего
    /// звучат в начале встречи (перекличка, «передаю слово»), поэтому берём с начала.
    /// </summary>
    private static string BuildMessage(IReadOnlyList<TranscriptEntry> entries, int budget, bool english)
    {
        var sb = new StringBuilder(english
            ? "Transcript (work out who is who):\n\n"
            : "Транскрипт (определи, кто есть кто):\n\n");

        int tokens = OllamaClient.EstimateTokens(sb.ToString());
        foreach (var e in entries)
        {
            var line = $"{e.Speaker}: {e.Text}";
            int size = OllamaClient.EstimateTokens(line);
            if (tokens + size > budget) break;
            sb.AppendLine(line);
            tokens += size;
        }
        return sb.ToString();
    }
}
