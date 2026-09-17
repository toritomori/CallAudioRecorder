using System;
using System.Windows;
using System.Windows.Interop;

namespace CallAudioRecorder;

/// <summary>
/// Рамка изменения размера для окна без системного обрамления.
///
/// Одного <c>ResizeMode="CanResize"</c> мало: при <c>WindowStyle="None"</c> вместе
/// с <c>AllowsTransparency="True"</c> у окна не остаётся неклиентской области, все попадания
/// курсора Windows считает попаданиями в клиент — края нечем захватить и курсор над ними
/// не меняется. Поэтому на <c>WM_NCHITTEST</c> мы сами отвечаем, что курсор стоит на краю,
/// а тянет окно дальше уже система: с родными курсорами, прилипанием к краям экрана
/// и уважением к <see cref="FrameworkElement.MinWidth"/> / <see cref="FrameworkElement.MinHeight"/>.
/// </summary>
internal static class WindowResizeBorder
{
    /// <summary>
    /// Ширина зоны захвата. Ровно на столько отступает корневой Border под тень (14 px),
    /// плюс пара пикселей видимой рамки — чтобы курсор менялся и когда целятся в саму рамку,
    /// а не в прозрачный отступ вокруг неё.
    /// </summary>
    public const double EdgeThickness = 16;

    private const int WmNcHitTest = 0x0084;

    // Коды зон окна из WinUser.h: что вернём на WM_NCHITTEST, то система и будет тянуть.
    internal const int HtClient = 1;
    internal const int HtLeft = 10;
    internal const int HtRight = 11;
    internal const int HtTop = 12;
    internal const int HtTopLeft = 13;
    internal const int HtTopRight = 14;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;

    /// <summary>
    /// Вешает обработчик на окно. Вызывать после появления дескриптора —
    /// из <see cref="Window.OnSourceInitialized"/>.
    /// </summary>
    public static void Attach(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;

        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            // Развёрнутое окно за края не тянут, а у CanMinimize размер вообще фиксирован.
            if (msg != WmNcHitTest
                || window.ResizeMode != ResizeMode.CanResize
                || window.WindowState != WindowState.Normal)
                return IntPtr.Zero;

            // lParam — координаты курсора на экране: младшее слово X, старшее Y, оба со знаком.
            long packed = lParam.ToInt64();
            var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));

            Point local;
            try
            {
                // Переводит и в координаты окна, и из пикселей в DIP — масштаб системы учтён.
                local = window.PointFromScreen(screen);
            }
            catch (InvalidOperationException)
            {
                return IntPtr.Zero; // окно уже закрывается, источника презентации нет
            }

            int area = HitTest(local.X, local.Y, window.ActualWidth, window.ActualHeight);
            if (area == HtClient) return IntPtr.Zero; // середину отдаём WPF как обычно

            handled = true;
            return new IntPtr(area);
        });
    }

    /// <summary>
    /// На каком краю окна стоит точка (координаты — от левого верхнего угла окна, в DIP).
    /// Углы проверяются раньше сторон: в углу тянут сразу за две стороны.
    /// </summary>
    internal static int HitTest(double x, double y, double width, double height,
                                double edge = EdgeThickness)
    {
        // Зоны с противоположных сторон не должны пересечься на узком окне, иначе левый край
        // окажется и правым — при MinWidth 900 этого не случится, но проверка дешёвая.
        edge = Math.Min(edge, Math.Min(width, height) / 2);

        bool left = x >= 0 && x <= edge;
        bool right = x <= width && x >= width - edge;
        bool top = y >= 0 && y <= edge;
        bool bottom = y <= height && y >= height - edge;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return HtClient;
    }
}
