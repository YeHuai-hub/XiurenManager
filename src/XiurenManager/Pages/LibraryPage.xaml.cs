using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using XiurenDownloader;

namespace XiurenManager.Pages;

public sealed class ModelLibraryRow
{
    public string Category { get; init; } = LibraryPaths.DefaultCategory;
    public string Name { get; init; } = "";
    public int SetCount { get; init; }
    public int MediaCount { get; init; }
    public int Score { get; init; }
    public string Key => Category + "|" + Name;
    public string DisplayDetail => $"{Category} · {SetCount} 套 · {MediaCount} 个媒体";
}

internal sealed class SetCardRow : INotifyPropertyChanged
{
    private ImageSource? cover;
    private string placeholder = "正在载入封面";

    public LocalStat Item { get; init; } = new();
    public int Score { get; init; }
    public string TagsLabel { get; init; } = "";
    public bool IsCoverLoading { get; set; }
    public bool CoverAttempted { get; set; }
    public string Title => Item.Title;
    public string MediaLabel => Item.VideoCount + Item.InvalidVideoCount > 0
        ? $"{Item.ImageCount} 图  {Item.VideoCount + Item.InvalidVideoCount} 视"
        : $"{Item.ImageCount} 张";
    public string SizeLabel => $"{Item.StorageTier} · {FormatBytes(Item.TotalBytes)}";

    public ImageSource? Cover
    {
        get => cover;
        set
        {
            cover = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Placeholder));
        }
    }

    public string Placeholder
    {
        get => Cover == null ? placeholder : "";
        set
        {
            placeholder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Placeholder));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }
}

internal sealed class SetCardGroup
{
    public IReadOnlyList<SetCardRow> Items { get; init; } = [];
}

public partial class LibraryPage : Page
{
    private readonly AppState state = App.State;
    private readonly ObservableCollection<ModelLibraryRow> models = [];
    private SetCardRow[] currentCards = [];
    private int cardColumns;
    private CancellationTokenSource coverCts = new();
    private bool fileOperationRunning;
    private bool categoryFilterLoading;
    private int filterVersion;

    public LibraryPage()
    {
        InitializeComponent();
        ModelList.ItemsSource = models;
        Loaded += LibraryPage_OnLoaded;
        Unloaded += LibraryPage_OnUnloaded;
        SizeChanged += (_, _) => ConstrainBodyToViewport();
    }

    private void LibraryPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        state.DataChanged += State_OnDataChanged;
        LoadLibrary();
    }

    private void LibraryPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        coverCts.Cancel();
    }

    private void LoadLibrary()
    {
        ConstrainBodyToViewport();
        var selected = (ModelList.SelectedItem as ModelLibraryRow)?.Key;
        var selectedCategory = CategoryFilter.SelectedItem as string ?? "全部分类";
        categoryFilterLoading = true;
        var categoryItems = state.Database.LocalFiles
            .Select(x => x.Category)
            .Concat(LibraryPaths.Categories(state.Settings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Prepend("全部分类")
            .ToArray();
        CategoryFilter.ItemsSource = categoryItems;
        CategoryFilter.SelectedItem = categoryItems.Contains(
            selectedCategory,
            StringComparer.OrdinalIgnoreCase)
            ? categoryItems.First(x => x.Equals(
                selectedCategory,
                StringComparison.OrdinalIgnoreCase))
            : "全部分类";
        selectedCategory = CategoryFilter.SelectedItem as string ?? "全部分类";
        categoryFilterLoading = false;
        models.Clear();
        var localFiles = state.Database.LocalFiles
            .Where(x => selectedCategory.Equals("全部分类", StringComparison.OrdinalIgnoreCase) ||
                        x.Category.Equals(
                            selectedCategory,
                            StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var group in localFiles
                     .Where(x => Directory.Exists(x.LocalDir))
                     .GroupBy(x => new { x.Category, x.Model })
                     .Select(g => new ModelLibraryRow
                     {
                         Category = g.Key.Category,
                         Name = g.Key.Model,
                         SetCount = g.Count(),
                         MediaCount = g.Sum(x => x.ImageCount + x.VideoCount + x.InvalidVideoCount),
                         Score = g.Sum(state.Favorites.GetScore)
                     })
                     .OrderByDescending(x => x.Score)
                     .ThenBy(x => x.Category)
                     .ThenBy(x => x.Name))
        {
            models.Add(group);
        }

        var sets = localFiles.Length;
        var images = localFiles.Sum(x => x.ImageCount);
        var videos = localFiles.Sum(x => x.VideoCount + x.InvalidVideoCount);
        var categoryCount = localFiles
            .Select(x => x.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        LibrarySummary.Text =
            $"{categoryCount} 个分类 · {models.Count} 个人物 · {sets} 套 · " +
            $"{images:N0} 张图片 · {videos:N0} 个视频";
        if (models.Count == 0)
        {
            currentCards = [];
            SetCards.ItemsSource = Array.Empty<SetCardGroup>();
            EmptyLibrary.Visibility = Visibility.Visible;
            return;
        }

        var index = models.ToList().FindIndex(x =>
            x.Key.Equals(selected, StringComparison.OrdinalIgnoreCase));
        ModelList.SelectedIndex = index >= 0 ? index : 0;
    }

    private void ConstrainBodyToViewport()
    {
        if (ActualHeight <= 0) return;
        LibraryBody.Height = Math.Max(260, ActualHeight - 118);
    }

    private void LibraryPage_OnPreviewMouseWheel(
        object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        if (SetCards.IsMouseOver)
        {
            var setViewer = FindVisualChild<ScrollViewer>(SetCards);
            if (setViewer == null) return;
            var rowDelta = Math.Clamp(Math.Max(1, Math.Abs(e.Delta) / 120), 1, 3);
            setViewer.ScrollToVerticalOffset(
                Math.Clamp(
                    setViewer.VerticalOffset - Math.Sign(e.Delta) * rowDelta,
                    0,
                    setViewer.ScrollableHeight));
            e.Handled = true;
            return;
        }

        if (!ModelList.IsMouseOver) return;
        var modelViewer = FindVisualChild<ScrollViewer>(ModelList);
        if (modelViewer == null) return;
        var itemDelta = Math.Clamp(Math.Max(1, Math.Abs(e.Delta) / 40), 1, 9);
        modelViewer.ScrollToVerticalOffset(
            Math.Clamp(
                modelViewer.VerticalOffset - Math.Sign(e.Delta) * itemDelta,
                0,
                modelViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void RefreshCards()
    {
        coverCts.Cancel();
        coverCts.Dispose();
        coverCts = new CancellationTokenSource();
        currentCards = [];
        SetCards.ItemsSource = Array.Empty<SetCardGroup>();

        if (ModelList.SelectedItem is not ModelLibraryRow model)
        {
            FilterResultText.Text = "";
            EmptyLibrary.Visibility = Visibility.Visible;
            return;
        }

        var filter = SetFilter.Text.Trim();
        var terms = filter.Split(
            [' ', '\t', ',', '，', ';', '；', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = state.Database.LocalFiles
            .Where(x => x.Category.Equals(
                model.Category,
                StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Model.Equals(model.Name, StringComparison.OrdinalIgnoreCase))
            .Where(x => terms.Length == 0 || MatchesFilter(x, terms))
            .Select(x => new SetCardRow
            {
                Item = x,
                Score = state.Favorites.GetScore(x),
                TagsLabel = string.Join(" · ", state.Favorites.GetTags(x).Take(3))
            });
        if (FavoriteOnly.IsChecked == true)
            values = values.Where(x => x.Score > 0);

        currentCards = values
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.LastScanned)
            .ThenBy(x => x.Title)
            .ToArray();
        FilterResultText.Text = $"{currentCards.Length} 套";
        EmptyLibrary.Visibility = currentCards.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildCardGroups();
        Dispatcher.BeginInvoke(() =>
            FindVisualChild<ScrollViewer>(SetCards)?.ScrollToTop());
    }

    private void RebuildCardGroups()
    {
        var availableWidth = SetCards.ActualWidth > 0
            ? SetCards.ActualWidth - 20
            : Math.Max(220, LibraryBody.ActualWidth - 260);
        var columns = Math.Max(1, (int)(availableWidth / 234));
        cardColumns = columns;

        var groups = new List<SetCardGroup>((currentCards.Length + columns - 1) / columns);
        for (var index = 0; index < currentCards.Length; index += columns)
        {
            groups.Add(new SetCardGroup
            {
                Items = currentCards.Skip(index).Take(columns).ToArray()
            });
        }
        SetCards.ItemsSource = groups;
    }

    private void SetCards_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (currentCards.Length == 0) return;
        var columns = Math.Max(1, (int)(Math.Max(220, e.NewSize.Width - 20) / 234));
        if (columns != cardColumns)
            RebuildCardGroups();
    }

    private async void SetCard_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SetCardRow card } ||
            card.Cover != null ||
            card.IsCoverLoading ||
            card.CoverAttempted)
        {
            return;
        }

        var token = coverCts.Token;
        card.IsCoverLoading = true;
        try
        {
            card.Cover = await MediaCoverService.LoadCoverAsync(
                card.Item,
                state.Settings,
                token);
            card.CoverAttempted = true;
            if (card.Cover == null)
                card.Placeholder = "无可用封面";
        }
        catch (OperationCanceledException) { }
        catch
        {
            card.CoverAttempted = true;
            card.Placeholder = "封面加载失败";
        }
        finally
        {
            card.IsCoverLoading = false;
        }
    }

    private void State_OnDataChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) LoadLibrary();
    }

    private void ModelList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshCards();
    }

    private async void SetFilter_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var version = ++filterVersion;
        await Task.Delay(150);
        if (IsLoaded && version == filterVersion)
            RefreshCards();
    }

    private bool MatchesFilter(LocalStat item, IReadOnlyList<string> terms)
    {
        var tags = state.Favorites.GetTags(item);
        return terms.All(term =>
            item.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            item.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            item.Model.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            item.LocalDir.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private void FavoriteOnly_OnChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RefreshCards();
    }

    private void CategoryFilter_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (IsLoaded && !categoryFilterLoading)
            LoadLibrary();
    }

    private void SetCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LocalStat item }) return;
        var context = currentCards.Select(card => card.Item).ToArray();
        var viewer = new ViewerWindow(item, context)
        {
            Owner = Window.GetWindow(this)
        };
        viewer.ShowDialog();
        LoadLibrary();
    }

    private void ManageSet_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OpenSetFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var item = MenuItemSet(sender);
        if (item == null || !Directory.Exists(item.LocalDir)) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(item.LocalDir);
        Process.Start(start);
    }

    private async void RenameSet_OnClick(object sender, RoutedEventArgs e)
    {
        var item = MenuItemSet(sender);
        if (item == null || !await CanEditFilesAsync()) return;

        var dialog = new TextInputWindow(
            "重命名套图",
            "输入新的套图文件夹名称",
            Path.GetFileName(item.LocalDir))
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true) return;

        var parent = Directory.GetParent(item.LocalDir)?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return;
        var target = Path.Combine(parent, dialog.ResultText);
        if (PathsEqual(item.LocalDir, target)) return;
        if (Directory.Exists(target))
        {
            await ShowInfoAsync("无法重命名", "目标名称已经存在，请换一个名称。");
            return;
        }

        var oldPath = item.LocalDir;
        var succeeded = await RunFileOperationAsync(
            "正在重命名",
            () => Directory.Move(oldPath, target));
        if (!succeeded) return;
        UpdateMovedMetadata(item, target);
    }

    private async void MoveSet_OnClick(object sender, RoutedEventArgs e)
    {
        var item = MenuItemSet(sender);
        if (item == null || !await CanEditFilesAsync()) return;
        var destination = PickDestination("选择移动后的父文件夹", item.LocalDir);
        if (destination == null) return;
        var target = Path.Combine(destination, Path.GetFileName(item.LocalDir));
        if (!await ValidateDestinationAsync(item.LocalDir, target)) return;

        var oldPath = item.LocalDir;
        var succeeded = await RunFileOperationAsync(
            "正在移动文件",
            () => FileSystem.MoveDirectory(oldPath, target, false));
        if (!succeeded) return;
        UpdateMovedMetadata(item, target);
    }

    private async void CopySet_OnClick(object sender, RoutedEventArgs e)
    {
        var item = MenuItemSet(sender);
        if (item == null || !await CanEditFilesAsync()) return;
        var destination = PickDestination("选择复制后的父文件夹", item.LocalDir);
        if (destination == null) return;
        var target = Path.Combine(destination, Path.GetFileName(item.LocalDir));
        if (!await ValidateDestinationAsync(item.LocalDir, target)) return;

        var succeeded = await RunFileOperationAsync(
            "正在复制文件",
            () => FileSystem.CopyDirectory(item.LocalDir, target, false));
        if (!succeeded) return;
        AddCopiedStat(item, target);
    }

    private async void DeleteSet_OnClick(object sender, RoutedEventArgs e)
    {
        var item = MenuItemSet(sender);
        if (item == null || !await CanEditFilesAsync()) return;
        var confirmed = await ConfirmAsync(
            "删除本套写真？",
            $"“{item.Title}”将被送入回收站。\n\n以后执行缺失资源检查时，工具仍可重新下载它。",
            "删除到回收站");
        if (!confirmed) return;

        var succeeded = await RunFileOperationAsync(
            "正在移入回收站",
            () => FileSystem.DeleteDirectory(
                item.LocalDir,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException));
        if (!succeeded) return;
        RemoveDeletedStat(item);
    }

    private static LocalStat? MenuItemSet(object sender)
    {
        return sender is MenuItem { DataContext: LocalStat item } ? item : null;
    }

    private async Task<bool> CanEditFilesAsync()
    {
        if (fileOperationRunning)
        {
            await ShowInfoAsync("文件操作正在进行", "请等待当前复制、移动或扫描完成。");
            return false;
        }
        if (state.Queue.IsRunning)
        {
            await ShowInfoAsync("下载队列正在运行", "请先停止下载队列，再编辑本地套图文件。");
            return false;
        }
        return true;
    }

    private static string? PickDestination(string title, string source)
    {
        var picker = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.GetParent(source)?.FullName ?? source,
            Multiselect = false
        };
        return picker.ShowDialog() == true ? picker.FolderName : null;
    }

    private async Task<bool> ValidateDestinationAsync(string source, string target)
    {
        if (PathsEqual(source, target))
        {
            await ShowInfoAsync("位置没有变化", "请选择其他父文件夹。");
            return false;
        }
        if (IsInside(target, source))
        {
            await ShowInfoAsync("无法使用该位置", "不能把套图复制或移动到它自己的子文件夹。");
            return false;
        }
        if (Directory.Exists(target))
        {
            await ShowInfoAsync("目标已存在", $"目标文件夹已经存在：\n{target}\n\n工具不会自动覆盖已有文件。");
            return false;
        }
        return true;
    }

    private async Task<bool> RunFileOperationAsync(string status, Action operation)
    {
        fileOperationRunning = true;
        FileOperationText.Text = status;
        FileOperationIndicator.Visibility = Visibility.Visible;
        SetCards.IsEnabled = false;
        ModelList.IsEnabled = false;
        state.WriteLog($"{status}: {(ModelList.SelectedItem as ModelLibraryRow)?.Name}");
        try
        {
            await Task.Run(operation);
            return true;
        }
        catch (Exception ex)
        {
            state.WriteLog($"文件操作失败: {ex.Message}");
            await ShowInfoAsync("文件操作失败", ErrorText.Format(ex));
            return false;
        }
        finally
        {
            fileOperationRunning = false;
            FileOperationIndicator.Visibility = Visibility.Collapsed;
            SetCards.IsEnabled = true;
            ModelList.IsEnabled = true;
        }
    }

    private void UpdateMovedMetadata(LocalStat item, string target)
    {
        var oldPath = NormalizePath(item.LocalDir);
        var oldCategory = item.Category;
        var oldModel = item.Model;
        var oldTitle = item.Title;
        var newModel = Directory.GetParent(target)?.Name ?? item.Model;
        var newTitle = Path.GetFileName(target);
        var newCategory = item.Category;
        TryGetTrackedLocation(target, out newCategory, out newModel, out newTitle);
        state.Favorites.UpdateLocation(item, target, newModel, newTitle);

        foreach (var resource in state.Database.Resources.Where(x =>
                     NormalizePath(x.LocalDir).Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                     (x.Category.Equals(oldCategory, StringComparison.OrdinalIgnoreCase) &&
                      x.Model.Equals(oldModel, StringComparison.OrdinalIgnoreCase) &&
                      x.Title.Equals(oldTitle, StringComparison.OrdinalIgnoreCase))))
        {
            resource.LocalDir = target;
            resource.Category = newCategory;
        }

        state.Database.LocalFiles.RemoveAll(x =>
            PathsEqual(x.LocalDir, oldPath) || PathsEqual(x.LocalDir, target));
        if (IsTrackedSetPath(target))
        {
            item.LocalDir = target;
            item.Category = newCategory;
            item.Model = newModel;
            item.Title = newTitle;
            item.LastScanned = DateTime.Now.ToString("s");
            state.Database.LocalFiles.Add(item);
        }

        state.Database.Save();
        state.WriteLog($"套图位置已更新: {oldPath} -> {target}");
        state.NotifyDataChanged();
    }

    private void AddCopiedStat(LocalStat source, string target)
    {
        if (!IsTrackedSetPath(target))
        {
            state.WriteLog($"套图已复制到统计范围外: {target}");
            return;
        }

        state.Database.LocalFiles.RemoveAll(x => PathsEqual(x.LocalDir, target));
        state.Database.LocalFiles.Add(new LocalStat
        {
            Category = TryGetTrackedLocation(
                target,
                out var category,
                out _,
                out _)
                ? category
                : source.Category,
            Model = Directory.GetParent(target)?.Name ?? source.Model,
            Title = Path.GetFileName(target),
            LocalDir = target,
            ImageCount = source.ImageCount,
            VideoCount = source.VideoCount,
            InvalidVideoCount = source.InvalidVideoCount,
            TotalBytes = source.TotalBytes,
            LastScanned = DateTime.Now.ToString("s")
        });
        state.Database.Save();
        state.WriteLog($"套图统计已新增: {target}");
        state.NotifyDataChanged();
    }

    private void RemoveDeletedStat(LocalStat item)
    {
        var path = item.LocalDir;
        state.Database.LocalFiles.RemoveAll(x => PathsEqual(x.LocalDir, path));
        state.Database.Save();
        state.WriteLog($"套图已移入回收站: {path}");
        state.NotifyDataChanged();
    }

    private bool IsTrackedSetPath(string path)
    {
        return TryGetTrackedLocation(path, out _, out _, out _);
    }

    private bool TryGetTrackedLocation(
        string path,
        out string category,
        out string model,
        out string title)
    {
        category = LibraryPaths.DefaultCategory;
        model = Directory.GetParent(path)?.Name ?? "";
        title = Path.GetFileName(path);
        var modelDir = Directory.GetParent(path);
        var categoryDir = modelDir?.Parent;
        if (categoryDir?.Parent != null &&
            PathsEqual(categoryDir.Parent.FullName, state.Settings.DownloadRoot))
        {
            category = categoryDir.Name;
            return true;
        }

        if (categoryDir != null && state.Settings.LegacyDownloadRoots.Any(root =>
                PathsEqual(categoryDir.FullName, root)))
        {
            category = LibraryPaths.DefaultCategory;
            return true;
        }
        return false;
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
    {
        var message = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Window.GetWindow(this),
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger
        };
        return await message.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    private async Task ShowInfoAsync(string title, string content)
    {
        var message = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Window.GetWindow(this),
            Title = title,
            Content = content,
            CloseButtonText = "确定"
        };
        await message.ShowDialogAsync();
    }

    private static bool PathsEqual(string left, string right)
    {
        return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInside(string candidate, string parent)
    {
        var candidatePath = NormalizePath(candidate);
        var parentPath = NormalizePath(parent);
        return candidatePath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Path.GetFullPath(value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async void Rescan_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button button)
            button.IsEnabled = false;
        try
        {
            await Task.Run(() => LocalScanner.Scan(state));
            LoadLibrary();
        }
        finally
        {
            if (sender is Wpf.Ui.Controls.Button completedButton)
                completedButton.IsEnabled = true;
        }
    }
}
