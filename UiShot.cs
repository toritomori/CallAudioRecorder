using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CallAudioRecorder;

/// <summary>
/// Снимок окна для проверки вёрстки: рендерит MainWindow в PNG и заодно считает
/// геометрию ключевых блоков, чтобы наложение элементов ловилось числом, а не на глаз.
///
/// Окно показывается за пределами экрана — снимок не зависит от того, какое окно
/// сейчас активно, и не захватывает чужие окна пользователя.
/// </summary>
public static class UiShot
{
    /// <summary>Блоки, которые не должны налезать друг на друга.</summary>
    private static readonly string[] Tracked =
        ["SettingsPanel", "SystemSource", "LanguageBox", "Timer", "StartStop", "Hint", "Status"];

    public static void Run(string pngPath, string logPath)
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
            window.Show();
            window.UpdateLayout();
            // Даём разметке досчитаться: списки устройств и языков заполняются в конструкторе.
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            int width = (int)window.ActualWidth;
            int height = (int)window.ActualHeight;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(pngPath)) encoder.Save(file);

            log.AppendLine($"окно {width}x{height} → {Path.GetFileName(pngPath)}");

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
            foreach (var (first, second) in new[]
                     {
                         ("SettingsPanel", "Timer"), ("SettingsPanel", "StartStop"), ("SettingsPanel", "Hint"),
                         ("StartStop", "Status"), ("Hint", "Status")
                     })
            {
                if (!boxes.TryGetValue(first, out var a) || !boxes.TryGetValue(second, out var b)) continue;
                var cross = Rect.Intersect(a, b);
                if (cross.IsEmpty || cross.Height < 1) continue;
                overlap = true;
                log.AppendLine($"НАЛОЖЕНИЕ: {first} × {second} — перекрытие {cross.Height:F0} px по высоте");
            }

            // Карточки должны помещаться целиком: прокрутка здесь — страховка на мелких экранах,
            // а не штатный режим. Обрезанный чекбокс читается пользователем как баг.
            if (window.FindName("SettingsScroll") is ScrollViewer scroll)
            {
                double hidden = scroll.ExtentHeight - scroll.ViewportHeight;
                log.AppendLine($"зона настроек: контент={scroll.ExtentHeight:F0} видно={scroll.ViewportHeight:F0}");
                if (hidden > 1)
                {
                    overlap = true;
                    log.AppendLine($"НЕ ВЛЕЗАЕТ: карточки настроек обрезаны на {hidden:F0} px — нужна прокрутка");
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
