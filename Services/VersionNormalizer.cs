using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CallAudioRecorder.Models;

namespace CallAudioRecorder.Services;

/// <summary>
/// Восстановление номеров версий: распознавание съедает точки, и «1.8.3» приезжает
/// как «183», «180» или «18.0». В итогах это выглядит как «релиз 183» — и модель либо
/// угадывает номер, либо переносит бессмыслицу в таблицу задач.
///
/// Опознать версию по одному числу нельзя: «180» — это и «1.8.0», и просто сто восемьдесят.
/// Поэтому работаем в два прохода по всей записи: сперва ищем числа, стоящие рядом со словом
/// «версия», «билд», «релиз» (или уже записанные канонически — «1.8.3»), и по ним выясняем,
/// какие семейства версий вообще звучали на этой встрече; затем нормализуем все числа
/// этих семейств, даже там, где подсказки рядом нет.
/// </summary>
public static class VersionNormalizer
{
    /// <summary>Слова, рядом с которыми число — почти наверняка версия.</summary>
    private static readonly Regex Marker = new(
        @"верси\w*|билд\w*|релиз\w*|мерж\w*|ветк\w*|version|build|release|branch",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Канонический номер: «1.8.3». По нему семейство видно без всяких подсказок.</summary>
    private static readonly Regex Canonical = new(@"\b(\d)\.(\d)\.(\d)\b", RegexOptions.Compiled);

    /// <summary>Кандидат: три цифры подряд или с одной точкой — «183», «18.0», «1.83».</summary>
    private static readonly Regex Candidate = new(@"\b\d[\d.,]{1,3}\b", RegexOptions.Compiled);

    /// <summary>Сколько символов вокруг числа считаем его окрестностью при поиске маркера.</summary>
    private const int MarkerWindow = 45;

    /// <summary>Сколько раз число должно прозвучать, чтобы одного соседства с маркером хватило.</summary>
    private const int MinRepeats = 5;

    /// <summary>
    /// Приводит номера версий к виду «1.8.3». Возвращает реплики с исправленным текстом;
    /// в <paramref name="log"/> (если передан) — пары «было → стало».
    /// </summary>
    public static IReadOnlyList<TranscriptEntry> Normalize(
        IReadOnlyList<TranscriptEntry> entries, ICollection<(string From, string To)>? log = null)
    {
        var families = CollectFamilies(entries);
        if (families.Count == 0) return entries;

        return entries.Select(e =>
        {
            var text = Apply(e.Text, families, log);
            return text == e.Text ? e : e with { Text = text, RawText = e.RawText ?? e.Text };
        }).ToList();
    }

    /// <summary>
    /// Семейства версий этой встречи («18» для 1.8.x): те, что подтверждены каноническим
    /// написанием или соседством со словом «версия»/«билд»/«релиз».
    /// </summary>
    public static HashSet<string> CollectFamilies(IReadOnlyList<TranscriptEntry> entries)
    {
        var confirmed = new HashSet<string>();     // каноническое «1.8.3» — доказательство само по себе
        var hints = new Dictionary<string, int>(); // соседство с «версией»: одного мало
        var repeats = new Dictionary<string, int>(); // как часто число вообще звучит на встрече

        foreach (var entry in entries)
        {
            foreach (Match m in Canonical.Matches(entry.Text))
                if (m.Groups[1].Value != "0") confirmed.Add(m.Groups[1].Value + m.Groups[2].Value);

            foreach (Match m in Candidate.Matches(entry.Text))
            {
                var digits = Digits(m.Value);
                if (digits.Length != 3 || digits[0] == '0') continue; // «000» из «10 000» — не версия
                if (PartOfLargerNumber(entry.Text, m)) continue;

                var family = digits[..2];
                repeats[family] = repeats.GetValueOrDefault(family) + 1;

                int from = Math.Max(0, m.Index - MarkerWindow);
                int to = Math.Min(entry.Text.Length, m.Index + m.Length + MarkerWindow);
                if (Marker.IsMatch(entry.Text[from..to]))
                    hints[family] = hints.GetValueOrDefault(family) + 1;
            }
        }

        // Одно совпадение рядом с «релизом» — это чаще всего просто число («сто процентов»,
        // «десять тысяч»), поэтому нужно либо второе такое соседство, либо число, звучащее
        // на встрече постоянно: номер версии повторяют, а случайную сотню — нет.
        foreach (var (family, count) in hints)
            if (count >= 2 || repeats.GetValueOrDefault(family) >= MinRepeats) confirmed.Add(family);

        return confirmed;
    }

    /// <summary>Число — часть более длинного, записанного с пробелом («10 000») или дефисом.</summary>
    private static bool PartOfLargerNumber(string text, Match m)
    {
        for (int i = m.Index - 1; i >= 0; i--)
        {
            if (text[i] == ' ') continue;
            return char.IsDigit(text[i]);
        }
        return false;
    }

    private static string Apply(string text, HashSet<string> families,
        ICollection<(string From, string To)>? log)
    {
        return Candidate.Replace(text, m =>
        {
            var digits = Digits(m.Value);
            if (digits.Length != 3 || digits[0] == '0' || !families.Contains(digits[..2])) return m.Value;
            if (PartOfLargerNumber(text, m)) return m.Value;

            var canonical = $"{digits[0]}.{digits[1]}.{digits[2]}";
            if (m.Value == canonical) return m.Value;

            log?.Add((m.Value, canonical));
            return canonical;
        });
    }

    private static string Digits(string token) =>
        new(token.Where(char.IsDigit).ToArray());
}
