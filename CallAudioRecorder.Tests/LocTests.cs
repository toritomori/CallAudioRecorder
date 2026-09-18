using System.Globalization;
using CallAudioRecorder.Services;

namespace CallAudioRecorder.Tests;

/// <summary>
/// Язык интерфейса по умолчанию: русский только на русской Windows, на любой другой —
/// английский. Сохранённый кнопкой выбор важнее языка системы.
/// </summary>
public class LocTests
{
    [Theory]
    [InlineData("ru-RU", false)]
    [InlineData("ru", false)]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    // Соседние языки — не русский: украинская или казахская Windows получает английский.
    [InlineData("uk-UA", true)]
    [InlineData("kk-KZ", true)]
    [InlineData("de-DE", true)]
    public void DefaultFollowsWindowsLanguage(string culture, bool english) =>
        Assert.Equal(english, Loc.DefaultEnglish(new CultureInfo(culture)));

    [Theory]
    [InlineData("English", "ru-RU", true)]
    [InlineData("Russian", "en-US", false)]
    [InlineData("english", "ru-RU", true)]
    // Не выбирали или в settings.json мусор — решает система.
    [InlineData(null, "ru-RU", false)]
    [InlineData(null, "en-US", true)]
    [InlineData("Klingon", "ru-RU", false)]
    [InlineData("Klingon", "fr-FR", true)]
    public void SavedChoiceBeatsWindowsLanguage(string? saved, string culture, bool english) =>
        Assert.Equal(english, Loc.Resolve(saved, new CultureInfo(culture)));
}
