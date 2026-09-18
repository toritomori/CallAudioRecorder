using System;
using System.Globalization;

namespace CallAudioRecorder.Services;

/// <summary>
/// Язык интерфейса: русский или английский. Не путать с языком распознавания
/// (<see cref="LanguageProfile"/>) — интерфейс на английском может записывать русскую
/// встречу и наоборот. От языка интерфейса зависят надписи окна, сообщения статуса
/// и имена файлов результатов; от языка записи — модель, метки спикеров и промпты LLM.
///
/// Строки обоих языков стоят рядом в месте использования (<see cref="T"/>, в XAML —
/// <c>{l:Tr 'русский', En='English'}</c>): языков два, и отдельные ресурсные файлы
/// только разносили бы пару по разным углам проекта.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Английский ли интерфейс. По умолчанию — русский: так сообщения сервисов в юнит-тестах
    /// не зависят от языка машины. Приложение выставляет значение при старте
    /// (<see cref="Resolve"/>) до создания окна.
    /// </summary>
    public static bool English { get; set; }

    /// <summary>Строка на текущем языке интерфейса.</summary>
    public static string T(string ru, string en) => English ? en : ru;

    /// <summary>
    /// Язык интерфейса по умолчанию: русский на русской системе, английский — на любой другой.
    /// Смотрим на язык интерфейса Windows (<see cref="CultureInfo.CurrentUICulture"/>), а не на
    /// региональные форматы: у человека с английской Windows и русскими датами окно должно
    /// заговорить по-английски.
    /// </summary>
    public static bool DefaultEnglish(CultureInfo culture) =>
        !string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Язык интерфейса с учётом сохранённого выбора: <paramref name="saved"/> — значение
    /// <c>uiLanguage</c> из settings.json («Russian»/«English»), null — пользователь ничего
    /// не выбирал, решает система.
    /// </summary>
    public static bool Resolve(string? saved, CultureInfo culture) => saved switch
    {
        _ when string.Equals(saved, "English", StringComparison.OrdinalIgnoreCase) => true,
        _ when string.Equals(saved, "Russian", StringComparison.OrdinalIgnoreCase) => false,
        _ => DefaultEnglish(culture)
    };
}
