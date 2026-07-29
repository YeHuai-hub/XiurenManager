using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

internal sealed class TaskRow
{
    public required JobItem Item { get; init; }
    public string Status => Item.Status;
    public string Label => QueueService.Label(Item);
    public string Target => Item.Target;
    public string Aliases => Item.Aliases;
    public string StartedAt => Item.StartedAt.Replace('T', ' ');
    public string FinishedAt => Item.FinishedAt.Replace('T', ' ');
    public string Error => Item.Error.Replace(Environment.NewLine, " | ");
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
        foreach (var item in selected) state.Database.Jobs.Remove(item);
        state.Database.Save();
        state.WriteLog($"已删除任务记录: {selected.Count}");
        state.NotifyJobsChanged();
    }

    private void ClearEnded_OnClick(object sender, RoutedEventArgs e)
    {
        Clear(x => x.Status is "Done" or "Failed" or "Canceled");
    }

    private void ClearAll_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning)
        {
            MessageBox.Show("请先停止当前任务，再清空队列。");
            return;
        }
        Clear(_ => true);
    }

    private void Clear(Func<JobItem, bool> predicate)
    {
        var count = state.Database.Jobs.RemoveAll(x => predicate(x));
        state.Database.Save();
        state.WriteLog($"已清除任务记录: {count}");
        state.NotifyJobsChanged();
    }
}
