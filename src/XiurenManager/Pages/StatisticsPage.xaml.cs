using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XiurenDownloader;

namespace XiurenManager.Pages;

internal sealed class StatisticsModelRow
{
    public string Category { get; init; } = LibraryPaths.DefaultCategory;
    public string Name { get; init; } = "";
    public int SetCount { get; init; }
    public int Score { get; init; }
    public string Key => Category + "|" + Name;
    public string DisplayName => $"[{Category}] {Name}";
}

internal sealed class StatisticsSetRow
{
    public required LocalStat Item { get; init; }
    public string Title => Item.Title;
    public int ImageCount => Item.ImageCount;
    public int VideoCount => Item.VideoCount;
    public int InvalidVideoCount => Item.InvalidVideoCount;
    public int Score { get; init; }
    public string SizeLabel => FormatBytes(Item.TotalBytes);

    private static string FormatBytes(long bytes)
    {
        var value = bytes / 1024d / 1024d;
        return value >= 1024 ? $"{value / 1024d:0.##} GB" : $"{value:0.##} MB";
    }
}

public partial class StatisticsPage : Page
{
    private readonly AppState state = App.State;
    private readonly ObservableCollection<StatisticsModelRow> models = [];
    private readonly ObservableCollection<StatisticsSetRow> sets = [];

    public StatisticsPage()
    {
        InitializeComponent();
        ModelGrid.ItemsSource = models;
        SetGrid.ItemsSource = sets;
        Loaded += StatisticsPage_OnLoaded;
        Unloaded += StatisticsPage_OnUnloaded;
    }

    private void StatisticsPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        state.DataChanged += State_OnDataChanged;
        Refresh();
    }

    private void StatisticsPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
    }

    private void State_OnDataChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void Refresh()
    {
        var selected = (ModelGrid.SelectedItem as StatisticsModelRow)?.Key;
        models.Clear();
        foreach (var group in state.Database.LocalFiles
                     .Where(x => Directory.Exists(x.LocalDir))
                     .GroupBy(x => new { x.Category, x.Model })
                     .Select(g => new StatisticsModelRow
                     {
                         Category = g.Key.Category,
                         Name = g.Key.Model,
                         SetCount = g.Count(),
                         Score = g.Sum(state.Favorites.GetScore)
                     })
                     .OrderByDescending(x => x.Score)
                     .ThenBy(x => x.Category)
                     .ThenBy(x => x.Name))
            models.Add(group);
        var index = models.ToList().FindIndex(x =>
            x.Key.Equals(selected, StringComparison.OrdinalIgnoreCase));
        ModelGrid.SelectedIndex = index >= 0 ? index : models.Count > 0 ? 0 : -1;
        var categoryCount = state.Database.LocalFiles
            .Select(x => x.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        StatisticsSummary.Text =
            $"{categoryCount} 个分类 · {models.Count} 个人物 · " +
            $"{state.Database.LocalFiles.Count} 套 · " +
            $"{state.Database.LocalFiles.Sum(x => x.ImageCount):N0} 张图片  ·  " +
            $"{state.Database.LocalFiles.Sum(x => x.VideoCount):N0} 个视频";
    }

    private void ModelGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        sets.Clear();
        if (ModelGrid.SelectedItem is not StatisticsModelRow model) return;
        foreach (var item in state.Database.LocalFiles
                     .Where(x => x.Category.Equals(
                         model.Category,
                         StringComparison.OrdinalIgnoreCase))
                     .Where(x => x.Model.Equals(model.Name, StringComparison.OrdinalIgnoreCase))
                     .Select(x => new StatisticsSetRow { Item = x, Score = state.Favorites.GetScore(x) })
                     .OrderByDescending(x => x.Score)
                     .ThenByDescending(x => x.Item.LastScanned))
            sets.Add(item);
    }

    private void OpenSelected_OnClick(object sender, RoutedEventArgs e) => OpenSelected();
    private void SetGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (SetGrid.SelectedItem is not StatisticsSetRow row) return;
        var context = sets.Select(item => item.Item).ToArray();
        new ViewerWindow(row.Item, context) { Owner = Window.GetWindow(this) }.ShowDialog();
        Refresh();
    }

    private async void Rescan_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await LocalScanner.ScanExclusiveAsync(state);
            Refresh();
        }
        catch (Exception ex)
        {
            state.WriteLog("资源统计扫描失败: " + ex.Message);
            MessageBox.Show(
                ErrorText.Format(ex),
                "扫描失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
