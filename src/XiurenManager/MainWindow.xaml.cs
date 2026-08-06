using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using XiurenManager.Pages;

namespace XiurenManager;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => App.State.Storage.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RecommendationPage));
        try
        {
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
            App.State.Storage.Start();
        }
    }

}
