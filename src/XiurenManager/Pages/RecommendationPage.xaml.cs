using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class RecommendationPage : Page
{
    private readonly AppState state = App.State;
    private CancellationTokenSource coverCts = new();
    private LocalStat? recommendation;
    private bool hasRecommended;

    public RecommendationPage()
    {
        InitializeComponent();
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
        else if (recommendation != null && RecommendationCover.Source == null)
        {
            _ = LoadCoverAsync(recommendation);
        }
    }

    private void RecommendationPage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        state.DataChanged -= State_OnDataChanged;
        coverCts.Cancel();
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
        await LoadCoverAsync(recommendation);
    }

    private void ShowRecommendation(LocalStat item)
    {
        EmptyMessage.Visibility = Visibility.Collapsed;
        RecommendationCover.Source = null;
        CoverPlaceholder.Visibility = Visibility.Visible;
        CoverProgress.Visibility = Visibility.Visible;
        CoverMessage.Text = "正在载入封面";
        ShuffleButton.IsEnabled = true;
        WatchButton.IsEnabled = true;
        OpenFolderButton.IsEnabled = true;

        ModelText.Text = item.Model;
        TitleText.Text = item.Title;
        MediaText.Text = $"{item.ImageCount:N0} 张图片  ·  " +
                         $"{item.VideoCount + item.InvalidVideoCount:N0} 个视频";
        SizeText.Text = FormatBytes(item.TotalBytes);
        ScoreText.Text = $"喜爱值  {state.Favorites.GetScore(item)}";
        TagItems.ItemsSource = state.Favorites.GetTags(item);
        RecommendationSummary.Text =
            $"从 {state.Database.LocalFiles.Count(IsEligible):N0} 套本地写真中随机挑选";
    }

    private async Task LoadCoverAsync(LocalStat item)
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

            RecommendationCover.Source = cover;
            CoverPlaceholder.Visibility = cover == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            CoverProgress.Visibility = Visibility.Collapsed;
            CoverMessage.Text = cover == null ? "这套写真没有可用封面" : "";
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
        RecommendationCover.Source = null;
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
        new ViewerWindow(recommendation, context)
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

    private static bool IsEligible(LocalStat item) =>
        Directory.Exists(item.LocalDir) &&
        item.ImageCount + item.VideoCount + item.InvalidVideoCount > 0;

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
}
