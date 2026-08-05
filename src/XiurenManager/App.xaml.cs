using System.Windows;

namespace XiurenManager;

public partial class App : Application
{
    internal static AppState State { get; } = new();

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            State.WriteLog("界面异常: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => arg.Equals("--scan-local", StringComparison.OrdinalIgnoreCase)))
        {
            LocalScanner.Scan(State, notify: false);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
