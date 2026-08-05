using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using XiurenManager.Pages;

namespace XiurenManager;

public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer migrationSyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(15)
    };
    private DateTime lastMigrationStateWriteUtc;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => migrationSyncTimer.Stop();
        migrationSyncTimer.Tick += (_, _) => SyncMigratedLocations();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ModelCategoryUnifier.ReconcileSplitModels(App.State, notify: false);
        LibraryLocationReconciler.Reconcile(App.State, notify: false);
        UpdateMigrationStateTimestamp();
        migrationSyncTimer.Start();
        RootNavigation.Navigate(typeof(RecommendationPage));
    }

    private void SyncMigratedLocations()
    {
        var stateFile = Path.Combine(
            XiurenDownloader.AppPaths.DataDir,
            "library-migration-state.json");
        if (!File.Exists(stateFile))
            return;
        var writeTime = File.GetLastWriteTimeUtc(stateFile);
        if (writeTime <= lastMigrationStateWriteUtc)
            return;
        lastMigrationStateWriteUtc = writeTime;
        LibraryLocationReconciler.Reconcile(App.State);
    }

    private void UpdateMigrationStateTimestamp()
    {
        var stateFile = Path.Combine(
            XiurenDownloader.AppPaths.DataDir,
            "library-migration-state.json");
        if (File.Exists(stateFile))
            lastMigrationStateWriteUtc = File.GetLastWriteTimeUtc(stateFile);
    }
}
