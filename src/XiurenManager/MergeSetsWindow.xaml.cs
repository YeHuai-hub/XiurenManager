using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Wpf.Ui.Controls;
using XiurenDownloader;

namespace XiurenManager;

internal sealed class MergePartRow : INotifyPropertyChanged
{
    private int order;
    public required LocalStat Item { get; init; }
    public int Order
    {
        get => order;
        set
        {
            order = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OrderLabel)));
        }
    }
    public string OrderLabel => $"{Order:00}";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MergeSetsWindow : FluentWindow
{
    private readonly ObservableCollection<MergePartRow> parts;
    public string ResultTitle { get; private set; } = "";
    internal IReadOnlyList<LocalStat> OrderedItems => parts.Select(row => row.Item).ToArray();

    internal MergeSetsWindow(IReadOnlyList<LocalStat> selected)
    {
        InitializeComponent();
        Title = "合并套图";
        var ordered = SetMergeService.AutoOrder(selected);
        parts = new ObservableCollection<MergePartRow>(ordered.Select((item, index) =>
            new MergePartRow { Item = item, Order = index + 1 }));
        PartList.ItemsSource = parts;
        PartList.SelectedIndex = 0;
        TitleEditor.Text = SetMergeService.SuggestTitle(ordered);
        App.State.WriteLog($"打开合并窗口: {parts.Count} 套");
    }

    private void MoveUp_OnClick(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_OnClick(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (PartList.SelectedItem is not MergePartRow row) return;
        var index = parts.IndexOf(row);
        var target = index + delta;
        if (target < 0 || target >= parts.Count) return;
        parts.Move(index, target);
        RefreshOrder();
        PartList.SelectedItem = row;
        PartList.ScrollIntoView(row);
    }

    private void RefreshOrder()
    {
        for (var index = 0; index < parts.Count; index++)
            parts[index].Order = index + 1;
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        var value = TitleEditor.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationText.Text = "合集名称不能为空";
            return;
        }
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ValidationText.Text = "名称包含 Windows 不允许的字符";
            return;
        }
        ResultTitle = value;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
