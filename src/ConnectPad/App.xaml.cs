using System.Windows;

namespace ConnectPad;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(CoreSelfTest.Run());
            return;
        }

        new MainWindow().Show();
    }
}
