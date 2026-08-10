using System.Windows;
using XiurenDownloader;

namespace XiurenManager;

public partial class App : Application
{
    private static AppState? state;
    private IDisposable? instanceLease;

    internal static AppState State => state ??
        throw new InvalidOperationException("应用状态尚未初始化。");

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            state?.WriteLog("界面异常: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instanceLease = ApplicationInstanceLock.TryAcquire();
        if (instanceLease == null)
        {
            if (e.Args.Length == 0)
                MessageBox.Show(
                    "写真资源管理器已经在运行。请先使用现有窗口，或退出后再执行命令行任务。",
                    "应用已运行");
            Shutdown(3);
            return;
        }
        state = new AppState();

        if (e.Args.Any(arg =>
                arg.Equals("--migrate-catalog", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(0);
            return;
        }

        var viewSetIndex = Array.FindIndex(e.Args, arg =>
            arg.Equals("--view-set", StringComparison.OrdinalIgnoreCase));
        if (viewSetIndex >= 0 && viewSetIndex + 1 < e.Args.Length)
        {
            var requestedPath = Path.GetFullPath(e.Args[viewSetIndex + 1]);
            var requestedSet = State.Database.LocalFiles.FirstOrDefault(item =>
                Path.GetFullPath(item.LocalDir)
                    .Equals(requestedPath, StringComparison.OrdinalIgnoreCase));
            if (requestedSet == null)
            {
                MessageBox.Show($"未在媒体库中找到套图：{requestedPath}", "诊断启动失败");
                Shutdown(2);
                return;
            }

            var viewer = new ViewerWindow(requestedSet);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = viewer;
            viewer.Show();
            return;
        }

        if (e.Args.Any(arg =>
                arg.Equals("--sync-set-metadata", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await State.Metadata.QueueAllAsync(announce: true);
            Shutdown(0);
            return;
        }

        if (e.Args.Any(arg => arg.Equals("--scan-local", StringComparison.OrdinalIgnoreCase)))
        {
            LocalScanner.ScanExclusive(State, notify: false);
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
            LocalScanner.ScanModelsExclusive(State, resources, notify: false);
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

        State.Queue.RecoverInterruptedJobs();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        state?.Catalog.Dispose();
        state?.Metadata.Dispose();
        state?.Storage.Dispose();
        instanceLease?.Dispose();
        instanceLease = null;
        base.OnExit(e);
    }
}
