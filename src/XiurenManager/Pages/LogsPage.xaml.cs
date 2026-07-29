using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class LogsPage : Page
{
    private readonly AppState state = App.State;

    public LogsPage()
    {
        InitializeComponent();
        LogList.ItemsSource = state.SessionLog;
        Loaded += LogsPage_OnLoaded;
        Unloaded += LogsPage_OnUnloaded;
    }

    private void LogsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.LogAdded -= State_OnLogAdded;
        state.LogAdded += State_OnLogAdded;
        ScrollToEnd();
    }

    private void LogsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.LogAdded -= State_OnLogAdded;
    }

    private void State_OnLogAdded(object? sender, string e)
    {
        if (IsLoaded) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (state.SessionLog.Count > 0)
            LogList.ScrollIntoView(state.SessionLog[^1]);
    }

    private void Clear_OnClick(object sender, RoutedEventArgs e) => state.SessionLog.Clear();

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDir) { UseShellExecute = true });
    }
}
