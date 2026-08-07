using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

internal sealed class TaskRow
{
    public required JobItem Item { get; init; }
    public string Status => Item.Status switch
    {
        "Queued" => "等待中",
        "Running" when !string.IsNullOrWhiteSpace(Item.Stage) => $"执行中 · {Item.Stage}",
        "Running" => "执行中",
        "Done" => "已完成",
        "Failed" => "失败",
        "Canceled" => "已停止",
        _ => Item.Status
    };
    public string Label => QueueService.Label(Item);
    public string Progress => ProgressText(Item);
    public string Target => Item.Target;
    public string Aliases => Item.Aliases;
    public string Exclusions => Item.Exclusions;
    public string StartedAt => Item.StartedAt.Replace('T', ' ');
    public string FinishedAt => Item.FinishedAt.Replace('T', ' ');
    public string Error => Item.Error.Replace(Environment.NewLine, " | ");

    private static string ProgressText(JobItem item)
    {
        if (item.ProgressTotal <= 0) return item.Stage == "搜索" ? "正在搜索资源" : "";
        var remaining = Math.Max(0, item.ProgressTotal - item.ProgressCompleted);
        var value = $"已下载 {item.ProgressCompleted}/{item.ProgressTotal} · 未下载 {remaining}";
        if (item.ProgressFailed > 0) value += $" · 失败 {item.ProgressFailed}";
        if (item.ProgressDeferred > 0) value += $" · 待继续 {item.ProgressDeferred}";
        return value;
    }
}

public partial class TasksPage : Page
{
    private readonly AppState state = App.State;
    private readonly ObservableCollection<TaskRow> rows = [];

    public TasksPage()
    {
        InitializeComponent();
        TaskGrid.ItemsSource = rows;
        Loaded += TasksPage_OnLoaded;
        Unloaded += TasksPage_OnUnloaded;
    }

    private void TasksPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.JobsChanged -= State_OnDataChanged;
        state.JobsChanged += State_OnDataChanged;
        Refresh();
    }

    private void TasksPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.JobsChanged -= State_OnDataChanged;
    }

    private void State_OnDataChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void Refresh()
    {
        var selected = TaskGrid.SelectedItems.Cast<TaskRow>().Select(x => x.Item).ToHashSet();
        rows.Clear();
        foreach (var item in state.Database.Jobs)
            rows.Add(new TaskRow { Item = item });
        foreach (var row in rows.Where(x => selected.Contains(x.Item)))
            TaskGrid.SelectedItems.Add(row);
        var running = rows.Count(x => x.Status.Equals("Running", StringComparison.OrdinalIgnoreCase));
        var queued = rows.Count(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
        TaskSummary.Text = $"{running} 个执行中  ·  {queued} 个等待  ·  {rows.Count} 条任务记录";

        var progressJob = state.Database.Jobs.FirstOrDefault(x =>
                              x.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) &&
                              x.ProgressTotal > 0)
                          ?? state.Database.Jobs.FirstOrDefault(x =>
                              x.ProgressTotal > 0 &&
                              !x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase))
                          ?? state.Database.Jobs.FirstOrDefault(x => x.ProgressTotal > 0);
        if (progressJob == null)
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var remaining = Math.Max(0, progressJob.ProgressTotal - progressJob.ProgressCompleted);
        var percent = progressJob.ProgressTotal == 0
            ? 0
            : progressJob.ProgressCompleted * 100d / progressJob.ProgressTotal;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressTitle.Text = QueueService.Label(progressJob);
        DownloadProgressPercent.Text = $"{percent:0.0}%";
        DownloadProgressBar.Maximum = Math.Max(1, progressJob.ProgressTotal);
        DownloadProgressBar.Value = progressJob.ProgressCompleted;
        DownloadProgressDetail.Text =
            $"总计 {progressJob.ProgressTotal} 套 · 已下载 {progressJob.ProgressCompleted} 套 · 未下载 {remaining} 套" +
            (progressJob.ProgressFailed > 0 ? $" · 失败 {progressJob.ProgressFailed} 套" : "") +
            (progressJob.ProgressDeferred > 0 ? $" · 待继续 {progressJob.ProgressDeferred} 套" : "");
    }

    private async void Continue_OnClick(object sender, RoutedEventArgs e) => await state.Queue.ContinueAsync();
    private void Stop_OnClick(object sender, RoutedEventArgs e) => state.Queue.Stop();

    private void DeleteSelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning)
        {
            MessageBox.Show("请先停止当前任务，再删除任务记录。");
            return;
        }
        var selected = TaskGrid.SelectedItems.Cast<TaskRow>().Select(x => x.Item).ToList();
        if (selected.Count == 0) return;
        var resumable = selected.Count(x => !x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase));
        if (resumable > 0 && MessageBox.Show(
                $"选中的任务中有 {resumable} 条尚未完成，删除后将无法从任务队列继续。确定删除吗？",
                "删除未完成任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        foreach (var item in selected) state.Database.Jobs.Remove(item);
        state.Database.Save();
        state.WriteLog($"已删除任务记录: {selected.Count}");
        state.NotifyJobsChanged();
    }

    private void ClearCompleted_OnClick(object sender, RoutedEventArgs e)
    {
        Clear(
            x => x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase),
            "已完成");
    }

    private void ClearAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning)
        {
            MessageBox.Show("请先停止当前任务，再清空队列。");
            return;
        }
        var unfinished = state.Database.Jobs.Count(x =>
            !x.Status.Equals("Done", StringComparison.OrdinalIgnoreCase));
        if (unfinished > 0 && MessageBox.Show(
                $"队列中还有 {unfinished} 条等待、取消或失败的未完成任务。清空后将无法继续，确定清空全部吗？",
                "清空全部任务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Clear(_ => true, "全部");
    }

    private void Clear(Func<JobItem, bool> predicate, string label)
    {
        var count = state.Database.Jobs.RemoveAll(x => predicate(x));
        state.Database.Save();
        state.WriteLog($"已清除{label}任务记录: {count}");
        state.NotifyJobsChanged();
    }
}
