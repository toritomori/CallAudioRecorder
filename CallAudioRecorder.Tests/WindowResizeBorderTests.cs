namespace CallAudioRecorder.Tests;

/// <summary>
/// Разметка краёв окна для изменения размера. Окно рисуется без системного обрамления,
/// поэтому какие попадания курсора считать краем, решает приложение: ошибка здесь означает,
/// что окно либо не тянется вовсе, либо тянется там, где пользователь кликает по контенту.
/// </summary>
public class WindowResizeBorderTests
{
    private const double W = 980, H = 920;

    private static int At(double x, double y) => WindowResizeBorder.HitTest(x, y, W, H);

    [Theory]
    // Углы важнее сторон: в углу тянут сразу за две стороны.
    [InlineData(0, 0, WindowResizeBorder.HtTopLeft)]
    [InlineData(15, 15, WindowResizeBorder.HtTopLeft)]
    [InlineData(W - 1, 0, WindowResizeBorder.HtTopRight)]
    [InlineData(0, H - 1, WindowResizeBorder.HtBottomLeft)]
    [InlineData(W - 1, H - 1, WindowResizeBorder.HtBottomRight)]
    public void Corners_WinOverSides(double x, double y, int expected) =>
        Assert.Equal(expected, At(x, y));

    [Theory]
    [InlineData(2, H / 2, WindowResizeBorder.HtLeft)]
    [InlineData(W - 2, H / 2, WindowResizeBorder.HtRight)]
    [InlineData(W / 2, 2, WindowResizeBorder.HtTop)]
    [InlineData(W / 2, H - 2, WindowResizeBorder.HtBottom)]
    public void Sides_AreGrabbable(double x, double y, int expected) =>
        Assert.Equal(expected, At(x, y));

    [Fact]
    public void Middle_StaysClient()
    {
        // Иначе клик по кнопке записи начинал бы тянуть окно.
        Assert.Equal(WindowResizeBorder.HtClient, At(W / 2, H / 2));
        Assert.Equal(WindowResizeBorder.HtClient, At(250, 780));
    }

    [Fact]
    public void ShadowGutterAndBorderItself_BothGrab()
    {
        // Корневой Border отступает на 14 px под тень: и прозрачный отступ, и пара пикселей
        // самой рамки должны ловить курсор — целятся обычно в видимый край.
        Assert.Equal(WindowResizeBorder.HtLeft, At(7, H / 2));   // тень
        Assert.Equal(WindowResizeBorder.HtLeft, At(15, H / 2));  // видимая рамка
        Assert.Equal(WindowResizeBorder.HtClient, At(20, H / 2)); // уже контент
    }

    [Fact]
    public void NarrowWindow_StillTellsSidesApart()
    {
        // На окне уже двух зон захвата (20 px при зоне 16) левый край не должен оказаться
        // заодно и правым. MinWidth такого не допустит, но правило не должно зависеть
        // от чужой настройки: зона ужимается до половины меньшей стороны.
        Assert.Equal(WindowResizeBorder.HtLeft, WindowResizeBorder.HitTest(1, 200, 20, 400));
        Assert.Equal(WindowResizeBorder.HtRight, WindowResizeBorder.HitTest(19, 200, 20, 400));
    }
}
