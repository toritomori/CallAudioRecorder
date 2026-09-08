using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CallAudioRecorder.Services;

/// <summary>
/// Приведение искажённых распознаванием терминов к написанию из глоссария — без LLM.
///
/// Зачем отдельно от <see cref="TranscriptCorrector"/>: чем реже звучит название, тем сильнее
/// его разносит распознавание (interstitial — 16 написаний и ни одного верного, Firebase — 10
/// написаний при одном верном), причём разброс идёт сразу по двум алфавитам: «Interstation»,
/// «Intersticial» и «интерстишул» — одно слово. LLM такой разнобой чинит ненадёжно: на реальной
/// записи она свела «Plavin» к выдуманному «Platin Max» вместо AppLovin. Здесь же сопоставление
/// детерминированное — придумать новое название оно не может в принципе.
///
/// Как: кириллица транслитерируется в латиницу, с обеих сторон берётся скелет согласных
/// («Interstation» и «интерстишул» → ntrsttn / ntrstsl), и слово притягивается к термину
/// глоссария, если скелеты различаются не больше, чем позволяет их длина. Короткие термины
/// (скелет меньше трёх согласных: Poki, DM, UW) сравниваются только целиком, вместе с гласными —
/// иначе «Дима» уехала бы в «DM», а «пока» в «Poki».
///
/// Целями служат ТОЛЬКО латинские записи глоссария: латинское название в русском тексте
/// не склоняется, поэтому подстановка не ломает грамматику. Кириллические записи остаются
/// hotwords'ами и подсказкой для LLM — заменять «миржа» на «мерж» нельзя, развалится падеж.
/// </summary>
public sealed class TermCanonizer
{
    /// <summary>
    /// Термин глоссария: канон, скелет согласных, полная звуковая форма (с гласными)
    /// и число слов. Форма с гласными нужна коротким терминам — им скелета мало.
    /// </summary>
    /// <param name="ProperNoun">
    /// Написан с заглавной, то есть имя собственное (Poki, AppLovin, Uniwars). Только такие
    /// термины заменяют кириллические слова: латинское название в русском тексте не склоняется,
    /// а вот нарицательные заимствования склоняются вовсю — «в конфигах» после замены на
    /// «config» разваливается. Регистром в глоссарии это и настраивается.
    /// </param>
    private sealed record Term(string Canon, string Skeleton, string Sound, int Words, bool ProperNoun);

    private readonly List<Term> _terms;

    /// <summary>
    /// Частотные слова живой речи, которые нельзя притягивать к терминам.
    /// Собраны по корпусу транскриптов: без них «пока» уезжает в «Poki», «пас» — в «Pass».
    /// Имена и термины самого глоссария сюда намеренно не попали.
    /// </summary>
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "ну", "вот", "там", "что", "это", "есть", "как", "мы", "так", "всё", "по", "просто", "нас",
        "будет", "бы", "если", "сейчас", "уже", "ещё", "может", "надо", "но", "тогда", "он", "можно",
        "тоже", "быть", "же", "ты", "нет", "чтобы", "типа", "пока", "потому", "угу", "тут", "вообще",
        "хорошо", "что-то", "наверное", "они", "меня", "нам", "или", "потом", "она", "для", "мне",
        "нужно", "всем", "было", "поэтому", "только", "сделать", "раз", "думаю", "знаю", "делать",
        "когда", "будем", "получается", "этот", "все", "за", "его", "даже", "какие-то", "их",
        "давайте", "общем", "как-то", "прямо", "тебя", "оно", "ли", "тебе", "из", "вроде", "понял",
        "здесь", "дальше", "её", "этом", "очень", "спасибо", "давай", "которые", "этого", "ничего",
        "до", "больше", "привет", "от", "эти", "посмотреть", "того", "эту", "именно", "всего",
        "такое", "этим", "вопрос", "например", "принципе", "можем", "конечно", "сразу", "время",
        "баланс", "много", "какой-то", "том", "такой", "был", "скорее", "короче", "смотреть",
        "будут", "кажется", "правильно", "недели", "сколько", "неделе", "сегодня", "возможно",
        "про", "будто", "сказать", "задачи", "равно", "кстати", "либо", "понимаю", "этой", "баги",
        "после", "момент", "где", "дня", "слово", "сильно", "стоит", "по-моему", "без", "неделю",
        "целом", "один", "при", "всех", "который", "должно", "точно", "чем", "выглядит", "хотя",
        "ладно", "лучше", "сам", "них", "буду", "где-то", "вы", "времени", "посмотрим", "опять",
        "нормально", "почему", "такие", "него", "проблема", "плане", "три", "такая", "поводу",
        "работает", "допустим", "понятно", "две", "со", "тем", "немножко", "пожалуйста", "хотел",
        "сделал", "могу", "чтоб", "всё-таки", "случае", "делаем", "можешь", "самом", "анимации",
        "вижу", "два", "говоря", "была", "плюс", "помню", "анимация", "задачу", "раньше", "проблемы",
        "идее", "долго", "виду", "смотри", "эта", "нету", "более", "хорошего", "примерно", "такого",
        "проверить", "под", "работать", "задач", "проще", "вопросы", "грубо", "правки", "деле",
        "себе", "какие", "всегда", "понять", "видимо", "часть", "юнитов", "начнём",
        "лид", "лиде", "лида", "мид", "тест", "теста", "тесте", "тесты",
    };

    private TermCanonizer(List<Term> terms) => _terms = terms;

    /// <summary>
    /// Строит канонизатор по глоссарию. Возвращает null, если латинских терминов в нём нет —
    /// тогда канонизировать нечего и вызывающему не нужно ничего делать.
    /// </summary>
    public static TermCanonizer? Build(string? glossary)
    {
        if (string.IsNullOrWhiteSpace(glossary)) return null;

        var terms = new List<Term>();
        foreach (var raw in Regex.Split(glossary, @"[,;\r\n]+"))
        {
            var canon = raw.Trim();
            if (canon.Length < 3 || !Regex.IsMatch(canon, "[A-Za-z]")) continue;

            var words = canon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 2) continue; // длинные фразы не сопоставляем — слишком много шансов промахнуться

            var sound = Sound(canon);
            if (sound.Length == 0) continue;
            terms.Add(new Term(canon, Skeleton(sound), sound, words.Length, char.IsUpper(canon[0])));
        }

        // Длинные термины пробуем первыми: «Battle Pass» должен победить отдельный «Pass».
        terms.Sort((a, b) => b.Words.CompareTo(a.Words));
        return terms.Count > 0 ? new TermCanonizer(terms) : null;
    }

    /// <summary>
    /// Заменяет искажённые термины каноническими. Пунктуация и регистр остального текста
    /// не меняются; в <paramref name="log"/> (если передан) складываются пары «было → стало».
    /// </summary>
    public string Apply(string text, ICollection<(string From, string To)>? log = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var tokens = Regex.Matches(text, @"[\p{L}\p{Nd}]+").Cast<Match>().ToList();
        if (tokens.Count == 0) return text;

        var sb = new StringBuilder();
        int pos = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            // Сперва пара слов («Крейзи Геймс» → «Crazy Games»), потом одиночное. Пару берём,
            // только если слова разделены пробелом или дефисом: иначе замена съест соседнюю
            // реплику вместе с запятой — «BattlePass, да» превратился бы в «Battle Pass».
            var pair = i + 1 < tokens.Count && PairAllowed(text, tokens[i], tokens[i + 1])
                ? Match2(tokens[i].Value, tokens[i + 1].Value)
                : null;
            var single = pair is null ? Match1(tokens[i].Value) : null;
            var hit = pair ?? single;
            if (hit is null) continue;

            var last = pair is not null ? tokens[i + 1] : tokens[i];
            var source = text[tokens[i].Index..(last.Index + last.Length)];
            if (source == hit)
            {
                if (pair is not null) i++; // пара уже канон — второе слово отдельно не разбираем
                continue;
            }

            sb.Append(text, pos, tokens[i].Index - pos).Append(hit);
            pos = last.Index + last.Length;
            log?.Add((source, hit));
            if (pair is not null) i++;
        }

        if (pos == 0) return text;
        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    /// <summary>
    /// Годятся ли два соседних слова в пару: они разделены только пробелом или дефисом
    /// («баттл-пасс») и оба сами по себе значимы. Без второго условия пара перетягивает
    /// на себя соседей — «на BattlePass» схлопнулось бы в «Battle Pass» вместе с предлогом.
    /// </summary>
    private static bool PairAllowed(string text, Match first, Match second)
    {
        for (int i = first.Index + first.Length; i < second.Index; i++)
            if (text[i] is not (' ' or '-' or ' ')) return false;

        return Meaningful(first.Value) && Meaningful(second.Value);

        static bool Meaningful(string word) => word.Length >= 3 && !Stop.Contains(word);
    }

    /// <summary>
    /// Одиночное слово сопоставляется и с двусловными терминами: «BattlePass» и «Battle Pass»
    /// звучат одинаково, а написано первое одним словом.
    /// </summary>
    private string? Match1(string word) => Match(word, pair: false);

    private string? Match2(string first, string second) => Match($"{first} {second}", pair: true);

    /// <summary>Ищет термин, к которому притягивается слово (или пара слов). null — не нашёлся.</summary>
    private string? Match(string source, bool pair)
    {
        if (Stop.Contains(source)) return null;

        var letters = source.Replace(" ", "");
        if (letters.Length < 4) return null; // «ТЗ», «два», «MVP» короче порога — только точное совпадение канона

        var sound = Sound(source);
        var skeleton = Skeleton(sound);
        if (skeleton.Length == 0) return null;

        bool cyrillic = Regex.IsMatch(source, "[А-Яа-яЁё]");
        bool digits = Regex.IsMatch(source, @"\d");

        string? best = null;
        int bestDistance = int.MaxValue;
        bool tie = false;

        foreach (var term in _terms)
        {
            if (pair && term.Words != 2) continue;
            if (cyrillic && !term.ProperNoun) continue;
            if (digits && !Regex.IsMatch(term.Canon, @"\d")) continue; // «MVP6» — это MVP шестой, цифру терять нельзя

            // Начало слова распознаётся надёжнее остального, и по нему отсеиваются совпадения
            // «серединой»: «наполовину» и «плагин» иначе притягиваются к AppLovin.
            if (Distance(Prefix(sound), Prefix(term.Sound), 1) > 1) continue;

            int distance;
            if (term.Skeleton.Length < 3)
            {
                // Скелета в две согласных мало, чтобы отличить термин от обычного слова:
                // у «DM» и «Дима» он общий. Такие термины ловим только целиком, по звучанию.
                if (term.Sound != sound) continue;
                distance = 0;
            }
            else
            {
                // Скелет — быстрый предварительный фильтр: он схлопывает гласные, которые
                // распознавание путает чаще всего, но по нему одному «задачки» неотличимы
                // от «SDK», а «были» от «build». Решает поэтому расстояние по звучанию целиком.
                int skeletonLimit = Tolerance(Math.Max(term.Skeleton.Length, skeleton.Length));
                if (Distance(skeleton, term.Skeleton, skeletonLimit) > skeletonLimit) continue;

                int limit = Tolerance(Math.Max(term.Sound.Length, sound.Length));
                distance = Distance(sound, term.Sound, limit);
                if (distance > limit) continue;
            }

            if (distance < bestDistance) { bestDistance = distance; best = term.Canon; tie = false; }
            else if (distance == bestDistance && !string.Equals(best, term.Canon, StringComparison.OrdinalIgnoreCase))
                tie = true;
        }

        return tie ? null : best; // ничья между разными терминами — безопаснее не трогать
    }

    /// <summary>Начало звуковой формы — по нему отсекаются совпадения «серединой слова».</summary>
    private static string Prefix(string sound) => sound.Length <= 2 ? sound : sound[..2];

    /// <summary>Сколько расхождений допустимо на такой длине: короткие слова требуют точности.</summary>
    private static int Tolerance(int length) => length switch
    {
        <= 5 => 1,
        <= 9 => 2,
        _ => 3,
    };

    /// <summary>
    /// Звуковая форма: кириллица → латиница, диграфы и созвучные буквы сведены к одной
    /// («c», «q», «k» → k; «z», «s» → s), удвоения схлопнуты. Гласные сохранены.
    /// </summary>
    private static string Sound(string source)
    {
        var sb = new StringBuilder(source.Length * 2);
        foreach (var ch in source.ToLowerInvariant())
            sb.Append(Translit(ch));

        var s = sb.ToString();
        s = s.Replace("shch", "s").Replace("sch", "s").Replace("sh", "s").Replace("ch", "c")
             .Replace("th", "t").Replace("ph", "f").Replace("ck", "k").Replace("kh", "h")
             .Replace("ts", "c").Replace("gh", "g");

        var result = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            char c = ch switch
            {
                'h' or '\'' or '-' or ' ' => '\0',
                'y' => 'i',
                'c' or 'q' or 'k' => 'k',
                'z' or 's' => 's',
                'w' or 'v' => 'v',
                'j' or 'g' => 'g',
                'x' => 'k', // «x» звучит как «кс», но согласная-то одна — считаем её k
                _ => char.IsLetter(ch) ? ch : '\0',
            };
            if (c != '\0' && (result.Length == 0 || result[^1] != c)) result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// Скелет согласных — звуковая форма без гласных. Именно гласные распознавание путает
    /// чаще всего: «Firebace», «Fairbace», «Farebace», «Firbaze» — один и тот же скелет.
    /// </summary>
    private static string Skeleton(string sound)
    {
        var result = new StringBuilder(sound.Length);
        foreach (var ch in sound)
            if (ch is not ('a' or 'e' or 'i' or 'o' or 'u')) result.Append(ch);
        return result.ToString();
    }

    private static string Translit(char ch) => ch switch
    {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e",
        'ж' => "g", 'з' => "z", 'и' => "i", 'й' => "i", 'к' => "k", 'л' => "l", 'м' => "m",
        'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u",
        'ф' => "f", 'х' => "h", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "shch",
        'ъ' => "", 'ы' => "i", 'ь' => "", 'э' => "e", 'ю' => "yu", 'я' => "ya",
        _ => ch.ToString(),
    };

    /// <summary>Расстояние Левенштейна с ранним выходом: считать дальше порога незачем.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit) return limit + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
