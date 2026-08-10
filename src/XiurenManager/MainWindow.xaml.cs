using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using XiurenDownloader;
using XiurenManager.Pages;

namespace XiurenManager;

public partial class MainWindow : FluentWindow
{
    private readonly bool resumeQueueAfterMaintenance;

    public MainWindow(bool resumeQueueAfterMaintenance = false)
    {
        this.resumeQueueAfterMaintenance = resumeQueueAfterMaintenance;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    internal void NavigateToLibrary(LocalStat item)
    {
        App.State.RequestLibraryNavigation(item);
        RootNavigation.Navigate(typeof(LibraryPage));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RecommendationPage));
        try
        {
            using var operationLease = await ResourceOperationLock.AcquireAsync(
                CancellationToken.None);
            await Task.Run(() =>
            {
                ModelCategoryUnifier.ReconcileSplitModels(App.State, notify: false);
                LibraryLocationReconciler.Reconcile(App.State, notify: false);
            });
        }
        catch (Exception ex)
        {
            App.State.WriteLog("启动维护未完成: " + ex.Message);
            System.Windows.MessageBox.Show(
                this,
                "程序已经启动，但后台维护未完成。\n\n" + ex.Message,
                "启动维护提示",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
        finally
        {
            if (resumeQueueAfterMaintenance)
            {
                try
                {
                    await App.State.Queue.ContinueAsync();
                }
                catch (Exception ex)
                {
                    App.State.WriteLog("启动后继续下载失败: " + ex.Message);
                }
            }
            App.State.Storage.Start();
            App.State.Catalog.StartBackgroundCoverBackfill();
            _ = App.State.Metadata.QueueStartupBackfillAsync();
        }
    }

}
