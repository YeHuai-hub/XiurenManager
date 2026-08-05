using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class SearchPage : Page
{
    private readonly AppState state = App.State;

    public SearchPage()
    {
        InitializeComponent();
        Loaded += SearchPage_OnLoaded;
        Unloaded += SearchPage_OnUnloaded;
    }

    private void SearchPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.JobsChanged -= State_OnDataChanged;
        state.JobsChanged += State_OnDataChanged;
        LoadSettings();
    }

    private void SearchPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.JobsChanged -= State_OnDataChanged;
    }

    private void State_OnDataChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) UpdateStatus();
    }

    private void LoadSettings()
    {
        SearchMode.SelectedIndex = state.Settings.SearchMode.Equals("Category", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CategoryPath.Text = state.Settings.CategoryPath;
        UpdateStatus();
    }

    private void SearchDownload_OnClick(object sender, RoutedEventArgs e) => Enqueue("SearchDownload");
    private void SearchOnly_OnClick(object sender, RoutedEventArgs e) => Enqueue("Search");

    private void Enqueue(string type)
    {
        var target = CanonicalName.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("请先填写主名称。", "搜索下载");
            return;
        }

        SaveSearchSettings();
        state.Queue.Enqueue(
            type,
            target,
            Aliases.Text,
            Exclusions.Text,
            Number(PageCount.Text, 999, 1, 9999),
            Number(MaxReady.Text, 9999, 0, 999999));
        UpdateStatus();
    }

    private void DownloadReady_OnClick(object sender, RoutedEventArgs e)
    {
        SaveSearchSettings();
        state.Queue.Enqueue("DownloadReady", "");
        UpdateStatus();
    }

    private async void Continue_OnClick(object sender, RoutedEventArgs e)
    {
        await state.Queue.ContinueAsync();
        UpdateStatus();
    }

    private void Stop_OnClick(object sender, RoutedEventArgs e)
    {
        state.Queue.Stop();
        UpdateStatus();
    }

    private void SaveSearchSettings()
    {
        state.Settings.SearchMode = (SearchMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Global";
        state.Settings.CategoryPath = string.IsNullOrWhiteSpace(CategoryPath.Text) ? "/tbgx" : CategoryPath.Text.Trim();
        state.Settings.Save();
    }

    private void UpdateStatus()
    {
        var running = state.Database.Jobs.Count(x => x.Status.Equals("Running", StringComparison.OrdinalIgnoreCase));
        var queued = state.Database.Jobs.Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
        QueueStatus.Text = state.Queue.IsRunning
            ? $"队列运行中：{running} 个执行中，{queued} 个等待"
            : queued > 0
                ? $"队列已暂停：{queued} 个任务等待继续"
                : "队列空闲，可继续添加搜索任务";
    }

    private static int Number(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
}
