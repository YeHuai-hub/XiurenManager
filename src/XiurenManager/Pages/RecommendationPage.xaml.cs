using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using XiurenDownloader;

namespace XiurenManager.Pages;

public sealed class RecommendationPreviewRow : INotifyPropertyChanged
{
    private ImageSource? thumbnail;

    public string Path { get; init; } = "";
    public bool IsLoading { get; set; }
    public bool LoadAttempted { get; set; }
    public ImageSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class RecommendationPage : Page
{
    private readonly AppState state = App.State;
    private readonly ObservableCollection<RecommendationPreviewRow> previews = [];
    private CancellationTokenSource coverCts = new();
    private CancellationTokenSource selectionCts = new();
    private LocalStat? recommendation;
    private bool hasRecommended;
    private bool suppressPreviewSelection;

    public RecommendationPage()
    {
        InitializeComponent();
        PreviewStrip.ItemsSource = previews;
        Loaded += RecommendationPage_OnLoaded;
        Unloaded += RecommendationPage_OnUnloaded;
    }

    private void RecommendationPage_OnLoaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        state.DataChanged += State_OnDataChanged;
        if (!hasRecommended)
        {
            hasRecommended = true;
            PickRecommendation();
        }
        else if (recommendation != null && RecommendationImageBrush.ImageSource == null)
        {
            _ = LoadRecommendationVisualsAsync(recommendation);
        }
        else if (recommendation != null)
        {
            RestartPreviewThumbnailLoading();
        }
    }

    private void RecommendationPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        coverCts.Cancel();
        selectionCts.Cancel();
    }

    private void State_OnDataChanged(object? sender, EventArgs e)
    {
        if (recommendation == null || !Directory.Exists(recommendation.LocalDir))
            PickRecommendation();
    }

    private async void PickRecommendation()
    {
        var candidates = state.Database.LocalFiles
            .Where(IsEligible)
            .ToArray();
        if (recommendation != null && candidates.Length > 1)
        {
            candidates = candidates
                .Where(item => !SameSet(item, recommendation))
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            recommendation = null;
            ShowEmpty();
            return;
        }

        recommendation = candidates[Random.Shared.Next(candidates.Length)];
        ShowRecommendation(recommendation);
        await LoadRecommendationVisualsAsync(recommendation);
    }

    private void ShowRecommendation(LocalStat item)
    {
        EmptyMessage.Visibility = Visibility.Collapsed;
        SetStageImage(null);
        previews.Clear();
        PreviewStrip.SelectedIndex = -1;
        CoverPlaceholder.Visibility = Visibility.Visible;
        CoverProgress.Visibility = Visibility.Visible;
        CoverMessage.Text = "正在载入封面";
        ShuffleButton.IsEnabled = true;
        WatchButton.IsEnabled = true;
        OpenFolderButton.IsEnabled = true;

        ModelText.Text = $"{item.Category} · {item.Model}";
        TitleText.Text = item.Title;
        MediaText.Text = $"{item.ImageCount:N0} 张图片  ·  " +
                         $"{item.VideoCount + item.InvalidVideoCount:N0} 个视频";
        SizeText.Text = FormatBytes(item.TotalBytes);
        ScoreText.Text = $"喜爱值  {state.Favorites.GetScore(item)}";
        TagItems.ItemsSource = state.Favorites.GetTags(item);
        RecommendationSummary.Text =
            $"从 {state.Database.LocalFiles.Count(IsEligible):N0} 套本地写真中随机挑选";
    }

    private async Task LoadRecommendationVisualsAsync(LocalStat item)
    {
        coverCts.Cancel();
        coverCts.Dispose();
        coverCts = new CancellationTokenSource();
        var token = coverCts.Token;
        try
        {
            var cover = await MediaCoverService.LoadCoverAsync(
                item,
                state.Settings,
                token,
                1200);
            if (token.IsCancellationRequested ||
                recommendation == null ||
                !SameSet(item, recommendation))
            {
                return;
            }

            SetStageImage(cover);
            CoverPlaceholder.Visibility = cover == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            CoverProgress.Visibility = Visibility.Collapsed;
            CoverMessage.Text = cover == null ? "这套写真没有可用封面" : "";

            var paths = await MediaCoverService.FindPreviewMediaAsync(
                item,
                state.Settings,
                int.MaxValue,
                token);
            foreach (var path in paths)
                previews.Add(new RecommendationPreviewRow { Path = path });
            suppressPreviewSelection = true;
            PreviewStrip.SelectedIndex = previews.Count > 0 ? 0 : -1;
            suppressPreviewSelection = false;
            _ = LoadAllPreviewThumbnailsAsync(previews.ToArray(), token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            CoverProgress.Visibility = Visibility.Collapsed;
            CoverMessage.Text = "封面载入失败";
            state.WriteLog($"推荐封面载入失败: {item.Title} | {ex.Message}");
        }
    }

    private void ShowEmpty()
    {
        SetStageImage(null);
        previews.Clear();
        CoverPlaceholder.Visibility = Visibility.Collapsed;
        EmptyMessage.Visibility = Visibility.Visible;
        ModelText.Text = "";
        TitleText.Text = "";
        MediaText.Text = "";
        SizeText.Text = "";
        ScoreText.Text = "";
        TagItems.ItemsSource = null;
        RecommendationSummary.Text = "完成本地扫描后，这里会随机推荐写真";
        ShuffleButton.IsEnabled = false;
        WatchButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
    }

    private void Shuffle_OnClick(object sender, RoutedEventArgs e)
    {
        PickRecommendation();
    }

    private void Watch_OnClick(object sender, RoutedEventArgs e)
    {
        if (recommendation == null) return;
        var context = state.Database.LocalFiles
            .Where(IsEligible)
            .OrderBy(item => item.Model)
            .ThenBy(item => item.Title)
            .ToArray();
        new ViewerWindow(
            recommendation,
            context,
            ViewerSetNavigationMode.Random)
        {
            Owner = Window.GetWindow(this)
        }.ShowDialog();
        ScoreText.Text = $"喜爱值  {state.Favorites.GetScore(recommendation)}";
        TagItems.ItemsSource = state.Favorites.GetTags(recommendation);
    }

    private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (recommendation == null || !Directory.Exists(recommendation.LocalDir)) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(recommendation.LocalDir);
        Process.Start(start);
    }

    private async void PreviewStrip_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPreviewSelection ||
            PreviewStrip.SelectedItem is not RecommendationPreviewRow preview)
        {
            return;
        }

        selectionCts.Cancel();
        selectionCts.Dispose();
        selectionCts = new CancellationTokenSource();
        var token = selectionCts.Token;
        try
        {
            var image = await MediaCoverService.LoadMediaPreviewAsync(
                preview.Path,
                state.Settings,
                token,
                1200);
            if (!token.IsCancellationRequested)
                SetStageImage(image);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                state.WriteLog($"推荐预览载入失败: {preview.Path} | {ex.Message}");
        }
    }

    private async Task LoadAllPreviewThumbnailsAsync(
        IEnumerable<RecommendationPreviewRow> rows,
        CancellationToken token)
    {
        var pending = new ConcurrentQueue<RecommendationPreviewRow>(rows);
        var workers = Enumerable.Range(0, 3)
            .Select(async _ =>
            {
                while (!token.IsCancellationRequested &&
                       pending.TryDequeue(out var row))
                {
                    await LoadPreviewThumbnailAsync(row, token);
                }
            });
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadPreviewThumbnailAsync(
        RecommendationPreviewRow row,
        CancellationToken token)
    {
        if (row.Thumbnail != null || row.IsLoading || row.LoadAttempted)
            return;
        row.IsLoading = true;
        try
        {
            row.Thumbnail = await MediaCoverService.LoadMediaPreviewAsync(
                row.Path,
                state.Settings,
                token,
                240);
            row.LoadAttempted = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            row.LoadAttempted = true;
            if (!token.IsCancellationRequested)
                state.WriteLog($"推荐缩略图载入失败: {row.Path} | {ex.Message}");
        }
        finally
        {
            row.IsLoading = false;
        }
    }

    private void PreviewImage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { DataContext: RecommendationPreviewRow row } ||
            row.Thumbnail != null ||
            coverCts.IsCancellationRequested)
        {
            return;
        }

        _ = LoadPreviewThumbnailAsync(row, coverCts.Token);
    }

    private void RestartPreviewThumbnailLoading()
    {
        coverCts.Cancel();
        coverCts.Dispose();
        coverCts = new CancellationTokenSource();
        var pending = previews
            .Where(row => row.Thumbnail == null && !row.LoadAttempted)
            .ToArray();
        foreach (var row in pending)
            row.IsLoading = false;
        if (pending.Length > 0)
            _ = LoadAllPreviewThumbnailsAsync(pending, coverCts.Token);
    }

    private void PreviewStrip_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = FindVisualChild<ScrollViewer>(PreviewStrip);
        if (viewer == null || viewer.ScrollableWidth <= 0) return;
        var itemDelta = Math.Clamp(
            Math.Max(1, Math.Abs(e.Delta) / 120),
            1,
            3);
        viewer.ScrollToHorizontalOffset(
            Math.Clamp(
                viewer.HorizontalOffset - Math.Sign(e.Delta) * itemDelta,
                0,
                viewer.ScrollableWidth));
        e.Handled = true;
    }

    private bool IsEligible(LocalStat item)
    {
        if (!Directory.Exists(item.LocalDir) ||
            item.ImageCount + item.VideoCount + item.InvalidVideoCount <= 0)
        {
            return false;
        }

        try
        {
            var extensions = state.Settings.ImageExts
                .Concat(state.Settings.VideoExts)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(item.LocalDir, "*", SearchOption.AllDirectories)
                .Any(path =>
                    !AppPaths.IsInsideTool(path) &&
                    MediaFileValidator.HasContent(path) &&
                    extensions.Contains(Path.GetExtension(path)));
        }
        catch
        {
            return false;
        }
    }

    private static bool SameSet(LocalStat left, LocalStat right) =>
        Path.GetFullPath(left.LocalDir)
            .Equals(Path.GetFullPath(right.LocalDir), StringComparison.OrdinalIgnoreCase);

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

    private void SetStageImage(ImageSource? image)
    {
        RecommendationImageBrush.ImageSource = image;
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
}
