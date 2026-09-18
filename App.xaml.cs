using System.Windows;

namespace CallAudioRecorder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Скрытые режимы самопроверки (Diagnostics/): отработать, записать лог и выйти без окна.
        if (Diagnostics.DiagnosticModes.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        // Язык интерфейса решается до создания окна: надписи разметки берутся при её загрузке.
        // Сохранённый выбор важнее, без него — русский на русской Windows, английский на любой другой.
        Services.Loc.English = Services.Loc.Resolve(CallAudioRecorder.MainWindow.ReadSavedUiLanguage(),
            System.Globalization.CultureInfo.CurrentUICulture);

        new MainWindow().Show();
    }
}
