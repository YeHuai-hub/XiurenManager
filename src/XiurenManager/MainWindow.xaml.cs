using System.Windows;
using Wpf.Ui.Controls;
using XiurenManager.Pages;

namespace XiurenManager;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(typeof(RecommendationPage));
    }
}
