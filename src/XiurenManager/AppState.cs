using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using XiurenDownloader;

namespace XiurenManager;

internal sealed record LibraryNavigationTarget(
    string Category,
    string Model);

internal sealed class AppState
{
    public Settings Settings { get; } = Settings.Load();
    public Database Database { get; } = Database.Load();
    public FavoriteStore Favorites { get; } = FavoriteStore.Load();
    public ObservableCollection<string> SessionLog { get; } = [];
    public QueueService Queue { get; }
    public StorageMigrationService Storage { get; }
    public MetadataSidecarCoordinator Metadata { get; }
    public LibraryCatalogService Catalog { get; }
    private readonly Channel<string> persistentLog = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private LibraryNavigationTarget? pendingLibraryNavigation;

    public event EventHandler? DataChanged;
    public event EventHandler? JobsChanged;
    public event EventHandler<string>? LogAdded;

    public AppState()
    {
        Catalog = new LibraryCatalogService(this);
        Queue = new QueueService(this);
        Storage = new StorageMigrationService(this);
        Metadata = new MetadataSidecarCoordinator(this);
        _ = Task.Run(PersistLogsAsync);
    }

    public void NotifyDataChanged()
    {
        RaiseOnUi(() => DataChanged?.Invoke(this, EventArgs.Empty));
    }

    public void NotifyJobsChanged()
    {
        RaiseOnUi(() => JobsChanged?.Invoke(this, EventArgs.Empty));
    }

    public void RequestLibraryNavigation(LocalStat item)
    {
        pendingLibraryNavigation = new LibraryNavigationTarget(
            item.Category,
            item.Model);
    }

    public LibraryNavigationTarget? ConsumeLibraryNavigation()
    {
        var target = pendingLibraryNavigation;
        pendingLibraryNavigation = null;
        return target;
    }

    private static void RaiseOnUi(Action callback)
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess())
            callback();
        else
            dispatcher.BeginInvoke(callback, DispatcherPriority.Background);
    }

    public void WriteLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var value = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
        persistentLog.Writer.TryWrite(value);
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        void AddToSession()
        {
            SessionLog.Add(value);
            while (SessionLog.Count > 2000)
                SessionLog.RemoveAt(0);
            LogAdded?.Invoke(this, value);
        }

        if (dispatcher.CheckAccess())
            AddToSession();
        else
            dispatcher.BeginInvoke(AddToSession, DispatcherPriority.Background);
    }

    private async Task PersistLogsAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var nextCleanup = DateTime.UtcNow.AddHours(1);
            await foreach (var value in persistentLog.Reader.ReadAllAsync())
            {
                try
                {
                    var path = LogMaintenance.CurrentLogPath("-wpf", Settings.LogMaxFileMB);
                    await File.AppendAllTextAsync(
                        path,
                        value + Environment.NewLine,
                        Encoding.UTF8);

                    if (DateTime.UtcNow >= nextCleanup)
                    {
                        LogMaintenance.Cleanup(Settings);
                        nextCleanup = DateTime.UtcNow.AddHours(1);
                    }
                }
                catch { }
            }
        }
        catch { }
    }
}
