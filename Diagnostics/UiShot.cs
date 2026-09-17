using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CallAudioRecorder.Diagnostics;

/// <summary>
/// Снимок окна для проверки вёрстки: рендерит MainWindow в PNG и заодно считает
/// геометрию ключевых блоков, чтобы наложение элементов ловилось числом, а не на глаз.
///
/// Окно показывается за пределами экрана — снимок не зависит от того, какое окно
/// сейчас активно, и не захватывает чужие окна пользователя.
/// </summary>
public static class UiShot
{
    /// <summary>
    /// Высота, начиная с которой карточки настроек обязаны помещаться целиком.
    /// Ниже — окно просто маленькое, и прокрутка настроек штатна.
    /// </summary>
    private const double RoomyHeight = 880;

    /// <summary>Блоки, которые не должны налезать друг на друга.</summary>
    private static readonly string[] Tracked =
        ["SettingsPanel", "SystemSource", "LanguageBox", "Timer", "StartStop", "Hint", "Status"];

    /// <param name="width">Ширина окна для снимка; 0 — как задано в XAML.</param>
    /// <param name="height">Высота окна для снимка; 0 — как задано в XAML.</param>
    /// <param name="sample">Заполнить окно образцом реплик и показать полосу задачи.</param>
    public static void Run(string pngPath, string logPath, double width = 0, double height = 0, bool sample = false)
    {
        var log = new StringBuilder();
        try
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000,
                Top = -4000,
                ShowInTaskbar = false
            };
            // Окно тянется, поэтому вёрстку надо проверять не только в стартовом размере:
            // размер ставим до Show, иначе разметка успевает посчитаться дважды.
            if (width > 0) window.Width = Math.Max(width, window.MinWidth);
            if (height > 0) window.Height = Math.Max(height, window.MinHeight);
            window.Show();
            if (sample) window.FillLayoutSample();
            window.UpdateLayout();
            // Даём разметке досчитаться: списки устройств и языков заполняются в конструкторе.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            int shotWidth = (int)window.ActualWidth;
            int shotHeight = (int)window.ActualHeight;
            var bitmap = new RenderTargetBitmap(shotWidth, shotHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(pngPath)) encoder.Save(file);

            log.AppendLine($"окно {shotWidth}x{shotHeight} → {Path.GetFileName(pngPath)}");

            // Геометрия блоков в координатах окна
            var boxes = new System.Collections.Generic.Dictionary<string, Rect>();
            foreach (var name in Tracked)
            {
                if (window.FindName(name) is not FrameworkElement element) continue;
                var box = BoundsOf(element, window);
                boxes[name] = box;
                log.AppendLine($"{name,-15} x={box.X,6:F0} y={box.Y,6:F0} w={box.Width,6:F0} h={box.Height,6:F0} " +
                               $"низ={box.Bottom,6:F0}");
            }

            bool overlap = false;

            // Карточки должны помещаться целиком — но только когда высота это позволяет.
            // Окно тянется, и на низком окне прокрутка настроек штатна: это и есть та самая
            // страховка, ради которой блок записи вынесен в отдельную строку.
            double hiddenSettings = 0;
            if (window.FindName("SettingsScroll") is ScrollViewer scroll)
            {
                hiddenSettings = scroll.ExtentHeight - scroll.ViewportHeight;
                log.AppendLine($"зона настроек: контент={scroll.ExtentHeight:F0} видно={scroll.ViewportHeight:F0}");
                if (hiddenSettings > 1)
                {
                    string verdict = window.ActualHeight >= RoomyHeight ? "НЕ ВЛЕЗАЕТ" : "прокрутка настроек";
                    log.AppendLine($"{verdict}: карточки настроек обрезаны на {hiddenSettings:F0} px");
                    if (window.ActualHeight >= RoomyHeight) overlap = true;
                }
            }

            foreach (var (first, second) in new[]
                     {
                         ("SettingsPanel", "Timer"), ("SettingsPanel", "StartStop"), ("SettingsPanel", "Hint"),
                         ("StartStop", "Status"), ("Hint", "Status")
                     })
            {
                if (!boxes.TryGetValue(first, out var a) || !boxes.TryGetValue(second, out var b)) continue;
                var cross = Rect.Intersect(a, b);
                if (cross.IsEmpty || cross.Height < 1) continue;
                // Прокрученные настройки обрезаны ScrollViewer'ом, а TransformToAncestor отдаёт
                // геометрию без учёта отсечения — пересечение здесь мнимое.
                if (hiddenSettings > 1 && first == "SettingsPanel")
                {
                    log.AppendLine($"(пересечение {first} × {second} — внутри прокрутки, не считается)");
                    continue;
                }
                overlap = true;
                log.AppendLine($"НАЛОЖЕНИЕ: {first} × {second} — перекрытие {cross.Height:F0} px по высоте");
            }

            // Лента — главный контент правой колонки: если она ужалась в полоску,
            // окно перестало быть полезным, даже когда ничего ни на что не наехало.
            if (window.FindName("TranscriptScroll") is FrameworkElement feed)
            {
                log.AppendLine($"лента реплик: h={feed.ActualHeight:F0}");
                if (feed.ActualHeight < 120)
                {
                    overlap = true;
                    log.AppendLine($"ЛЕНТА СХЛОПНУЛАСЬ: {feed.ActualHeight:F0} px — меньше 120");
                }
            }

            // Всё ли влезло в окно по вертикали
            foreach (var (name, box) in boxes)
                if (box.Bottom > window.ActualHeight + 1)
                {
                    overlap = true;
                    log.AppendLine($"ЗА ГРАНИЦЕЙ ОКНА: {name} низ={box.Bottom:F0} > {window.ActualHeight:F0}");
                }

            window.Close();
            log.Insert(0, overlap ? "FAIL\n" : "OK\n");
        }
        catch (Exception ex)
        {
            log.Insert(0, "FAIL\n");
            log.AppendLine(ex.ToString());
        }
        File.WriteAllText(logPath, log.ToString(), Encoding.UTF8);
    }

    private static Rect BoundsOf(FrameworkElement element, Visual root)
    {
        var transform = element.TransformToAncestor(root);
        return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }
}
