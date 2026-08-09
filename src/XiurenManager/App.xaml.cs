using System.Windows;
using XiurenDownloader;

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
        if (e.Args.Any(arg =>
                arg.Equals("--sync-set-metadata", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await State.Metadata.QueueAllAsync(announce: true);
            State.Metadata.Dispose();
            Shutdown(0);
            return;
        }

        if (e.Args.Any(arg => arg.Equals("--scan-local", StringComparison.OrdinalIgnoreCase)))
        {
            LocalScanner.Scan(State, notify: false);
            Shutdown(0);
            return;
        }

        var scanModelIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--scan-local-model", StringComparison.OrdinalIgnoreCase));
        if (scanModelIndex >= 0 && scanModelIndex + 2 < e.Args.Length)
        {
            var category = LibraryPaths.NormalizeCategory(e.Args[scanModelIndex + 1]);
            var model = XiurenClient.Safe(e.Args[scanModelIndex + 2]);
            var resources = State.Database.Resources.Where(x =>
                    x.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
                    x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            LocalScanner.ScanModels(State, resources, notify: false);
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
