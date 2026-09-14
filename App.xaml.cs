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

        new MainWindow().Show();
    }
}
