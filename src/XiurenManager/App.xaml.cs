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

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg => arg.Equals("--scan-local", StringComparison.OrdinalIgnoreCase)))
        {
            LocalScanner.Scan(State, notify: false);
            Shutdown(0);
            return;
        }

        var migrateIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--migrate-storage-model", StringComparison.OrdinalIgnoreCase));
        if (migrateIndex >= 0 && migrateIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await State.Storage.MoveNamedModelAsync(e.Args[migrateIndex + 1]);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(arg =>
                arg.Equals("--migrate-storage-batch", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await State.Storage.RunBatchAsync(manual: true);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
