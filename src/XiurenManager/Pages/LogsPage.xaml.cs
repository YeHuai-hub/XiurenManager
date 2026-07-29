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

    private async void ClearDisk_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定删除日志目录中的全部 .log 文件吗？\n当前显示也会一并清空，新的运行日志会自动重新创建。",
                "清理磁盘日志",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        ClearDiskButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(LogMaintenance.ClearAll);
            state.SessionLog.Clear();
            state.WriteLog(
                $"手动日志清理: 删除 {result.Files} 个文件，释放 {result.Bytes / 1024d / 1024d:0.##} MB，失败 {result.FailedFiles} 个");
            MessageBox.Show(
                $"已删除 {result.Files} 个日志文件。\n释放 {result.Bytes / 1024d / 1024d:0.##} MB。" +
                (result.FailedFiles > 0 ? $"\n有 {result.FailedFiles} 个文件正在使用或无法删除。" : ""),
                "清理完成");
        }
        finally
        {
            ClearDiskButton.IsEnabled = true;
        }
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogDir) { UseShellExecute = true });
    }
}
