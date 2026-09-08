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
/// Зачем: диаризация различает голоса, но не знает, кого как зовут, — и таблица задач
/// в итогах выходит с исполнителями «Собеседник 12». При этом на планёрках люди постоянно
/// обращаются друг к другу по имени («Передаю слово Артёму», «Алексей, ты хотел сказать?»),
/// так что имя обычно стоит прямо в соседней реплике. Модель для итогов уже угадывает эту
/// связь сама, но наполовину и только внутри своего ответа — здесь она делается один раз
/// и попадает в сам транскрипт.
///
/// Выдумывать имена модели не даём: имя принимается, только если оно действительно
/// встречается в транскрипте.
/// </summary>
public static class SpeakerNamer
{
    /// <summary>Сколько токенов резервировать под ответ: несколько строк «метка = имя».</summary>
    private const int ResponseTokens = 512;

    private const string SystemRu =
        """
        Ты определяешь по транскрипту встречи, как зовут её участников.

        Спикеры размечены автоматически по голосу: «Я» — владелец записи, «Собеседник 1»,
        «Собеседник 2», … — остальные. Имена участников звучат в самом разговоре: к людям
        обращаются по имени, передают слово, представляются.

        Ответь строками строго вида:
        Я = Имя | точная цитата из транскрипта
        Собеседник 1 = Имя | точная цитата из транскрипта
        Собеседник 2 = ?

        Правила:
        - Первая строка — имя владельца записи (метка «Я»), если оно прозвучало.
        - Дальше одна строка на каждую метку из транскрипта, в том же написании.
        - Цитата — несколько слов ДОСЛОВНО из транскрипта, по которым видно, что это тот
          самый человек: обращение к нему, передача слова, представление.
        - ВАЖНО: обращение по имени указывает на того, КОМУ говорят, а не на говорящего.
          Если «Собеседник 3» произносит «Антон, сорри, поправлю», то Антон — это НЕ
          Собеседник 3, а тот, кто отвечает следующим. Цитату бери из реплики ДРУГОГО спикера.
        - Имя — как его произносят в разговоре, в именительном падеже («Артёму» → «Артём»).
        - Если имя не прозвучало или ты не уверен — ставь «?» без цитаты. Догадки хуже, чем «?».
        - Не приписывай одно имя двум разным меткам.
        - Никаких пояснений, только строки «метка = имя | цитата».
        """;

    private const string SystemEn =
        """
        You work out the participants' names from a meeting transcript.

        Speakers are labelled automatically by voice: "Me" is the owner of the recording,
        "Speaker 1", "Speaker 2", … are the others. Their names are said in the conversation
        itself: people address each other by name, hand over the floor, introduce themselves.

        Answer with lines strictly in this form:
        Me = Name | exact quote from the transcript
        Speaker 1 = Name | exact quote from the transcript
        Speaker 2 = ?

        Rules:
        - The first line is the recording owner's name (label "Me"), if it was said.
        - Then one line per label found in the transcript, spelled the same way.
        - The quote is a few words taken VERBATIM from the transcript showing this is the
          person in question: someone addressing them, handing over the floor, introducing them.
        - IMPORTANT: addressing someone by name points at the person being spoken TO, not at
          the speaker. If "Speaker 3" says "Anton, sorry, let me correct you", then Anton is
          NOT Speaker 3 but whoever answers next. Take the quote from ANOTHER speaker's line.
        - Use the name as it is said in the conversation, in its base form.
        - If the name was not said, or you are unsure, put "?" with no quote. A guess is worse.
        - Do not give the same name to two different labels.
        - No explanations, only the "label = name | quote" lines.
        """;

    /// <summary>
    /// Определяет имена и возвращает реплики с заменёнными метками. Если ничего надёжного
    /// не нашлось, возвращает исходные реплики.
    /// </summary>
    public static async Task<IReadOnlyList<TranscriptEntry>> ApplyNamesAsync(
        OllamaClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile? language = null, string? ownerName = null, CancellationToken ct = default)
    {
        var names = await DetectAsync(ollama, model, entries, language, ct, ownerName: ownerName);
        if (names.Count == 0) return entries;

        return entries
            .Select(e => names.TryGetValue(e.Speaker, out var name) ? e with { Speaker = name } : e)
            .ToList();
    }

    /// <summary>Карта «метка спикера → имя». Пустая, если ничего не удалось определить.</summary>
    /// <param name="trace">Куда сложить сырой ответ модели и отброшенные строки — для самотеста.</param>
    public static async Task<IReadOnlyDictionary<string, string>> DetectAsync(
        OllamaClient ollama, string model, IReadOnlyList<TranscriptEntry> entries,
        LanguageProfile? language = null, CancellationToken ct = default,
        ICollection<string>? trace = null, string? ownerName = null)
    {
        var result = new Dictionary<string, string>();
        var labels = entries.Select(e => e.Speaker).Where(s => !Languages.IsMeLabel(s)).Distinct().ToList();
        if (labels.Count == 0) return result;

        bool english = language?.Language == TranscriptionLanguage.English;
        string system = english ? SystemEn : SystemRu;

        int budget = await ollama.GetInputBudgetAsync(model, ResponseTokens, ct);
        var user = BuildMessage(entries, budget - OllamaClient.EstimateTokens(system) - 256, english);
        // Температура 0: на 0.3 разметка менялась от прогона к прогону — то пять имён, то одно.
        var answer = await ollama.ChatAsync(model, system, user, ResponseTokens, ct: ct, temperature: 0);
        trace?.Add($"ответ модели:{Environment.NewLine}{answer.Trim()}");

        // Имя засчитывается, только если оно звучало в разговоре: так модель не может
        // «дорисовать» участника, которого в записи не было.
        var text = string.Join(" ", entries.Select(e => e.Text));
        var spoken = new HashSet<string>(
            Regex.Matches(text, @"\p{L}{2,}").Select(m => m.Value.ToLowerInvariant()));
        // Реплики по спикерам: по ним проверяется не только наличие цитаты, но и то,
        // что она взята у КОГО-ТО ДРУГОГО — к себе по имени не обращаются.
        var byLabel = entries
            .GroupBy(e => e.Speaker)
            .ToDictionary(g => g.Key, g => Normalize(string.Join(" ", g.Select(e => e.Text))));

        var lines = Regex.Matches(answer, @"^\s*([^=|]+?)\s*=\s*([^|]+?)\s*(?:\|\s*(.*?))?\s*$",
                RegexOptions.Multiline)
            .Select(m => (Label: m.Groups[1].Value.Trim(),
                          Name: m.Groups[2].Value.Trim().Trim('«', '»', '"', '.', ','),
                          Quote: m.Groups[3].Value.Trim()))
            .ToList();

        // Имя владельца записи собеседнику доставаться не должно: на планёрках к нему
        // обращаются чаще всех. Строке «Я = …» из общего ответа доверять нельзя — на трёх
        // реальных встречах она называла владельцем то одного собеседника, то другого,
        // а отдельный короткий вопрос каждый раз давал верное имя.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owner = string.IsNullOrWhiteSpace(ownerName)
            ? await AskOwnerAsync(ollama, model, user, english, ct)
            : ownerName.Trim();
        if (owner is not null) taken.Add(owner);
        trace?.Add($"владелец записи: {owner ?? "не определён"}" +
                   (string.IsNullOrWhiteSpace(ownerName) ? " (определён моделью)" : " (из настроек)"));

        foreach (var (label, name, quote) in lines)
        {
            // Саму метку «Я» не трогаем: на неё завязан TranscriptEntry.IsMe.
            if (!labels.Contains(label)) continue;
            if (name.Length < 2 || name == "?" || name.Contains(' ')) continue;
            if (taken.Contains(name)) continue;
            if (!spoken.Contains(name.ToLowerInvariant()) && !SpokenInflected(spoken, name)) continue;
            if (result.ContainsValue(name)) continue; // одно имя на двух метках — значит модель гадает
            var source = QuoteSource(byLabel, quote);
            if (source is null)
            {
                trace?.Add($"отброшено «{label} = {name}»: цитата «{quote}» в транскрипте не найдена");
                continue; // не смогла показать место в тексте — значит гадала
            }
            if (source == label && StartsWithName(quote, name))
            {
                trace?.Add($"отброшено «{label} = {name}»: в цитате «{quote}» он сам зовёт " +
                           "кого-то по имени, а обращение указывает на собеседника");
                continue;
            }

            result[label] = name;
        }
        return result;
    }

    /// <summary>Имя владельца записи (метка «Я»), или null, если модель его не назвала.</summary>
    private static async Task<string?> AskOwnerAsync(
        OllamaClient ollama, string model, string transcript, bool english, CancellationToken ct)
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
            responseTokens: 32, ct: ct, temperature: 0);
        var name = answer.Trim().Trim('«', '»', '"', '.', ',');
        return name.Length >= 2 && name != "?" && !name.Contains(' ') ? name : null;
    }

    /// <summary>
    /// Чью реплику цитирует модель, или null, если цитаты в транскрипте нет. Требование
    /// сослаться на конкретное место — главная защита от гадания (без него разметка менялась
    /// от прогона к прогону), а знание автора цитаты ловит перепутанное направление обращения.
    /// Сверяем по первым словам: дословно модель цитирует не всегда.
    /// </summary>
    private static string? QuoteSource(Dictionary<string, string> byLabel, string quote)
    {
        var normalized = Normalize(quote);
        if (normalized.Length < 8) return null; // «да» и «угу» ничего не подтверждают

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int take = words.Length; take >= 3; take--)
        {
            var prefix = string.Join(' ', words.Take(take));
            if (prefix.Length < 8) break;
            foreach (var (label, text) in byLabel)
                if (text.Contains(prefix, StringComparison.Ordinal)) return label;
        }
        return null;
    }

    /// <summary>
    /// Цитата начинается с этого имени — то есть это обращение («Антон, сорри, поправлю»),
    /// и названный человек как раз НЕ тот, кто говорит. Самопредставление («всем привет,
    /// я Марина») под правило не попадает, поэтому такие цитаты остаются в силе.
    /// </summary>
    private static bool StartsWithName(string quote, string name)
    {
        var words = Normalize(quote).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var stem = Normalize(name);
        if (stem.Length > 3) stem = stem[..^1]; // «Артёма» и «Артём» — одно обращение
        return words[0].StartsWith(stem, StringComparison.Ordinal);
    }

    /// <summary>Текст в сравнимом виде: нижний регистр, только буквы и цифры через пробел.</summary>
    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd}]+", " ").Trim();

    /// <summary>
    /// Имя могло звучать только в косвенном падеже («Передаю слово Артёму»), поэтому
    /// сверяем ещё и по основе — совпадения первых букв достаточно, имена короткие.
    /// </summary>
    private static bool SpokenInflected(HashSet<string> spoken, string name)
    {
        if (name.Length < 4) return false;
        var stem = name[..^1].ToLowerInvariant();
        return spoken.Any(w => w.Length >= stem.Length && w.StartsWith(stem, StringComparison.Ordinal));
    }

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
