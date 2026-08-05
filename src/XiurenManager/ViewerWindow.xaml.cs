using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Wpf.Ui.Controls;
using XiurenDownloader;

namespace XiurenManager;

public sealed class ViewerMediaRow
{
    public string Path { get; init; } = "";
    public bool IsVideo { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Type => IsVideo ? "视频" : "图片";
}

internal enum ViewerSetNavigationMode
{
    Sequential,
    Random
}

public partial class ViewerWindow : FluentWindow
{
    private readonly AppState state = App.State;
    private LocalStat set;
    private readonly IReadOnlyList<LocalStat> setContext;
    private readonly ViewerSetNavigationMode setNavigationMode;
    private int setIndex;
    private readonly ObservableCollection<ViewerMediaRow> media = [];
    private readonly ObservableCollection<string> tags = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer slideshowTimer = new();
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private Media? playingMedia;
    private bool draggingPosition;
    private bool immersive;
    private bool initialSetLoaded;
    private int imageLoadVersion;

    internal ViewerWindow(
        LocalStat set,
        IReadOnlyList<LocalStat>? context = null,
        ViewerSetNavigationMode navigationMode = ViewerSetNavigationMode.Sequential)
    {
        var availableSets = (context ?? [set])
            .Where(item => Directory.Exists(item.LocalDir))
            .DistinctBy(item => Path.GetFullPath(item.LocalDir), StringComparer.OrdinalIgnoreCase)
            .ToList();
        setIndex = availableSets.FindIndex(item => SameSet(item, set));
        if (setIndex < 0)
        {
            availableSets.Insert(0, set);
            setIndex = 0;
        }
        setContext = availableSets;
        setNavigationMode = navigationMode;
        this.set = setContext[setIndex];

        InitializeComponent();
        state.WriteLog($"打开浏览器: {this.set.Model} / {this.set.Title}");
        AutoPlaySpeed.Value = Math.Clamp(state.Settings.SlideshowSeconds, 0.5, 30);
        UpdateAutoPlaySpeed();
        TagItems.ItemsSource = tags;
        Filmstrip.ItemsSource = media;
        InitializePlayer();
        ContentRendered += ViewerWindow_OnContentRendered;
        timer.Tick += (_, _) => UpdatePlayback();
        timer.Start();
        slideshowTimer.Tick += (_, _) => SelectNextImage();
        Closed += (_, _) =>
        {
            slideshowTimer.Stop();
            state.Settings.SlideshowSeconds = AutoPlaySpeed.Value;
            state.Settings.Save();
            state.WriteLog($"关闭浏览器: {set.Model} / {set.Title}");
            DisposePlayer();
        };
    }

    private void ViewerWindow_OnContentRendered(object? sender, EventArgs e)
    {
        if (initialSetLoaded) return;
        initialSetLoaded = true;
        Dispatcher.BeginInvoke(
            () => LoadSet(set, selectLast: false, writeLog: false),
            DispatcherPriority.ContextIdle);
    }

    private void InitializePlayer()
    {
        try
        {
            Core.Initialize();
            libVlc = new LibVLC("--no-video-title-show", "--quiet");
            player = new MediaPlayer(libVlc) { Volume = (int)VolumeSlider.Value };
            VideoViewer.MediaPlayer = player;
            player.Playing += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Pause24));
            player.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Play24));
            player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Replay24));
            player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ViewerMessage.Text = "视频无法播放，请在统计页检查文件完整性";
                ViewerMessage.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            ViewerMessage.Text = "播放器初始化失败：" + ex.Message;
            ViewerMessage.Visibility = Visibility.Visible;
        }
    }

    private void LoadSet(LocalStat nextSet, bool selectLast, bool writeLog = true)
    {
        slideshowTimer.Stop();
        StopPlayback();
        imageLoadVersion++;
        media.Clear();
        tags.Clear();
        TagEditor.Clear();
        CloseTags();

        set = nextSet;
        SetTitle.Text = set.Title;
        foreach (var tag in state.Favorites.GetTags(set))
            tags.Add(tag);
        TagsStatus.Text = tags.Count == 0 ? "暂无标签" : $"已有 {tags.Count} 个标签";
        UpdateScore();
        LoadMedia(selectLast);

        if (writeLog)
            state.WriteLog($"切换浏览套图: {set.Model} / {set.Title}");
    }

    private void LoadMedia(bool selectLast)
    {
        if (!Directory.Exists(set.LocalDir))
        {
            ViewerMessage.Text = "本地目录不存在";
            return;
        }
        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(set.LocalDir, "*", SearchOption.AllDirectories)
                     .Where(x => !AppPaths.IsInsideTool(x))
                     .Where(x => MediaFileValidator.IsUsable(x, imageExts, videoExts))
                     .OrderBy(NaturalSortKey, StringComparer.OrdinalIgnoreCase))
            media.Add(new ViewerMediaRow { Path = path, IsVideo = videoExts.Contains(Path.GetExtension(path)) });

        if (media.Count == 0)
        {
            ViewerMessage.Text = "这套目录内没有图片或视频";
            return;
        }
        Filmstrip.SelectedIndex = selectLast ? media.Count - 1 : 0;
    }

    private async void ShowMedia(int index)
    {
        if (index < 0 || index >= media.Count) return;
        slideshowTimer.Stop();
        var item = media[index];
        var setPosition = setContext.Count > 1 ? $"套图 {setIndex + 1} / {setContext.Count}    " : "";
        MediaTitle.Text = $"{setPosition}{index + 1} / {media.Count}    {item.Name}";
        ViewerMessage.Text = "正在载入";
        ViewerMessage.Visibility = Visibility.Visible;
        if (item.IsVideo)
        {
            ShowVideo(item.Path);
            return;
        }

        imageLoadVersion++;
        var version = imageLoadVersion;
        StopPlayback();
        VideoViewer.Visibility = Visibility.Collapsed;
        ImageViewer.Visibility = Visibility.Visible;
        try
        {
            var source = await MediaCoverService.LoadViewerImageAsync(
                item.Path,
                state.Settings,
                CancellationToken.None);
            if (version != imageLoadVersion) return;
            ImageViewer.Source = source;
            ViewerMessage.Visibility = Visibility.Collapsed;
            RestartSlideshow();
        }
        catch (Exception ex)
        {
            if (version != imageLoadVersion) return;
            ViewerMessage.Text = "图片无法显示：" + ex.Message;
            RestartSlideshow();
        }
    }

    private void ShowVideo(string path)
    {
        imageLoadVersion++;
        ImageViewer.Source = null;
        ImageViewer.Visibility = Visibility.Collapsed;
        VideoViewer.Visibility = Visibility.Visible;
        StopPlayback();
        if (libVlc == null || player == null)
        {
            ViewerMessage.Text = "视频播放器未初始化";
            return;
        }
        try
        {
            playingMedia = new Media(libVlc, path, FromType.FromPath);
            if (!player.Play(playingMedia))
                throw new InvalidOperationException("播放器拒绝打开这个文件");
            ViewerMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ViewerMessage.Text = "视频无法播放：" + ex.Message;
            ViewerMessage.Visibility = Visibility.Visible;
        }
    }

    private void Filmstrip_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowMedia(Filmstrip.SelectedIndex);
    }

    private void Previous_OnClick(object sender, RoutedEventArgs e) => SelectRelative(-1);
    private void Next_OnClick(object sender, RoutedEventArgs e) => SelectRelative(1);

    private void SelectRelative(int delta)
    {
        var target = Filmstrip.SelectedIndex + delta;
        if (target >= 0 && target < media.Count)
        {
            Filmstrip.SelectedIndex = target;
            Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
            return;
        }

        if (!TrySwitchSet(Math.Sign(delta), selectLast: delta < 0))
            return;
        Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
    }

    private bool TrySwitchSet(int delta, bool selectLast)
    {
        if (setNavigationMode == ViewerSetNavigationMode.Random && delta > 0)
            return TrySwitchToRandomSet();

        for (var index = setIndex + delta; index >= 0 && index < setContext.Count; index += delta)
        {
            if (!HasMedia(setContext[index]))
                continue;
            setIndex = index;
            LoadSet(setContext[setIndex], selectLast);
            return true;
        }
        return false;
    }

    private bool TrySwitchToRandomSet()
    {
        if (setContext.Count < 2)
            return false;

        var candidates = Enumerable.Range(0, setContext.Count)
            .Where(index => index != setIndex)
            .ToList();
        while (candidates.Count > 0)
        {
            var position = Random.Shared.Next(candidates.Count);
            var candidateIndex = candidates[position];
            candidates[position] = candidates[^1];
            candidates.RemoveAt(candidates.Count - 1);
            if (!HasMedia(setContext[candidateIndex]))
                continue;

            setIndex = candidateIndex;
            LoadSet(setContext[setIndex], selectLast: false);
            return true;
        }
        return false;
    }

    private bool HasMedia(LocalStat candidate)
    {
        try
        {
            var extensions = state.Settings.ImageExts
                .Concat(state.Settings.VideoExts)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(candidate.LocalDir, "*", SearchOption.AllDirectories)
                .Any(path =>
                    !AppPaths.IsInsideTool(path) &&
                    extensions.Contains(Path.GetExtension(path)) &&
                    MediaFileValidator.IsUsable(
                        path,
                        state.Settings.ImageExts,
                        state.Settings.VideoExts));
        }
        catch
        {
            return false;
        }
    }

    private void AutoPlayToggle_OnChecked(object sender, RoutedEventArgs e)
    {
        if (Filmstrip.SelectedItem is ViewerMediaRow { IsVideo: true })
            SelectNextImage();
        else
            RestartSlideshow();
    }

    private void AutoPlayToggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
        slideshowTimer.Stop();
    }

    private void AutoPlaySpeed_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAutoPlaySpeed();
        RestartSlideshow();
    }

    private void UpdateAutoPlaySpeed()
    {
        if (AutoPlaySpeed == null || AutoPlaySpeedText == null) return;
        var seconds = Math.Clamp(AutoPlaySpeed.Value, 0.5, 30);
        slideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        AutoPlaySpeedText.Text = $"{seconds:0.#} 秒/张";
    }

    private void RestartSlideshow()
    {
        slideshowTimer.Stop();
        if (AutoPlayToggle?.IsChecked == true &&
            Filmstrip?.SelectedItem is ViewerMediaRow { IsVideo: false })
        {
            slideshowTimer.Start();
        }
    }

    private void SelectNextImage()
    {
        slideshowTimer.Stop();
        if (AutoPlayToggle?.IsChecked != true || media.Count == 0) return;

        var current = Math.Max(0, Filmstrip.SelectedIndex);
        if (setNavigationMode == ViewerSetNavigationMode.Random)
        {
            for (var next = current + 1; next < media.Count; next++)
            {
                if (media[next].IsVideo) continue;
                Filmstrip.SelectedIndex = next;
                Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
                return;
            }

            if (!TrySwitchToRandomSet())
                RestartSlideshow();
            return;
        }

        for (var offset = 1; offset <= media.Count; offset++)
        {
            var next = (current + offset) % media.Count;
            if (media[next].IsVideo) continue;
            if (next == current)
            {
                RestartSlideshow();
                return;
            }
            Filmstrip.SelectedIndex = next;
            Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
            return;
        }
    }

    private void PlayPause_OnClick(object sender, RoutedEventArgs e)
    {
        if (Filmstrip.SelectedItem is not ViewerMediaRow { IsVideo: true } item || player == null) return;
        if (player.State is VLCState.Ended or VLCState.Stopped)
            ShowVideo(item.Path);
        else if (player.IsPlaying)
            player.Pause();
        else
            player.Play();
    }

    private void ScorePlus_OnClick(object sender, RoutedEventArgs e) => ChangeScore(1);
    private void ScoreMinus_OnClick(object sender, RoutedEventArgs e) => ChangeScore(-1);

    private void ChangeScore(int delta)
    {
        var value = state.Favorites.ChangeScore(set, delta);
        UpdateScore();
        state.WriteLog($"{set.Model} / {set.Title} 喜爱值已改为 {value}");
        state.NotifyDataChanged();
    }

    private void UpdateScore()
    {
        ScoreText.Text = $"喜爱值  {state.Favorites.GetScore(set)}";
    }

    private void Tags_OnClick(object sender, RoutedEventArgs e)
    {
        if (TagsPanel.Visibility == Visibility.Visible)
        {
            CloseTags();
            return;
        }

        TagsPanel.Visibility = Visibility.Visible;
        TagEditor.Focus();
    }

    private void CloseTags_OnClick(object sender, RoutedEventArgs e)
    {
        CloseTags();
    }

    private void CloseTags()
    {
        TagsPanel.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void TagEditor_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTag();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseTags();
            e.Handled = true;
        }
    }

    private void AddTag_OnClick(object sender, RoutedEventArgs e)
    {
        AddTag();
    }

    private void AddTag()
    {
        var value = TagEditor.Text.Trim();
        if (value.Length == 0) return;
        if (value.Length > 30)
        {
            TagsStatus.Text = "单个标签最多 30 个字符";
            return;
        }
        if (tags.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            TagsStatus.Text = "这个标签已经存在";
            TagEditor.SelectAll();
            return;
        }
        if (tags.Count >= 30)
        {
            TagsStatus.Text = "每套最多保存 30 个标签";
            return;
        }

        tags.Add(value);
        TagEditor.Clear();
        SaveTags("标签已添加");
        TagEditor.Focus();
    }

    private void RemoveTag_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }) return;
        var existing = tags.FirstOrDefault(x =>
            x.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;
        tags.Remove(existing);
        SaveTags("标签已删除");
    }

    private void ClearTags_OnClick(object sender, RoutedEventArgs e)
    {
        if (tags.Count == 0) return;
        tags.Clear();
        SaveTags("标签已清空");
    }

    private void SaveTags(string status)
    {
        state.Favorites.SetTags(set, tags);
        TagsStatus.Text = $"{status}  {DateTime.Now:HH:mm:ss}";
        state.WriteLog($"{set.Model} / {set.Title} 标签已更新");
        state.NotifyDataChanged();
    }

    private void PositionSlider_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        draggingPosition = true;
        PositionSlider.CaptureMouse();
        SeekFromPointer(e);
        e.Handled = true;
    }

    private void PositionSlider_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!draggingPosition || e.LeftButton != MouseButtonState.Pressed) return;
        SeekFromPointer(e);
        e.Handled = true;
    }

    private void PositionSlider_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        SeekFromPointer(e);
        draggingPosition = false;
        PositionSlider.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void SeekFromPointer(MouseEventArgs e)
    {
        if (PositionSlider.ActualWidth <= 0) return;
        var position = e.GetPosition(PositionSlider);
        var ratio = Math.Clamp(position.X / PositionSlider.ActualWidth, 0, 1);
        PositionSlider.Value =
            PositionSlider.Minimum +
            ratio * (PositionSlider.Maximum - PositionSlider.Minimum);
        if (player is { Length: > 0 })
            player.Time = (long)(player.Length * ratio);
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (player != null) player.Volume = (int)e.NewValue;
    }

    private void UpdatePlayback()
    {
        if (player is not { Length: > 0 } || draggingPosition) return;
        PositionSlider.Value = Math.Clamp(player.Time * 1000d / player.Length, 0, 1000);
        TimeText.Text = $"{FormatTime(player.Time)} / {FormatTime(player.Length)}";
    }

    private void MediaCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleImmersive();
    }

    private void Immersive_OnClick(object sender, RoutedEventArgs e) => ToggleImmersive();

    private void ToggleImmersive()
    {
        if (!immersive)
            CloseTags();
        immersive = !immersive;
        HeaderRow.Height = immersive ? new GridLength(0) : new GridLength(54);
        FilmstripRow.Height = immersive ? new GridLength(0) : new GridLength(88);
        ControlsRow.Height = immersive ? new GridLength(0) : new GridLength(58);
        ViewerHeader.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        Filmstrip.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        PlaybackControls.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        CanvasPrevious.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        CanvasNext.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        WindowState = immersive ? WindowState.Maximized : WindowState.Normal;
    }

    private void ViewerWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox or
            System.Windows.Controls.Slider &&
            e.Key != Key.Escape)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left: SelectRelative(-1); break;
            case Key.Right: SelectRelative(1); break;
            case Key.Space: PlayPause_OnClick(sender, e); break;
            case Key.F11: ToggleImmersive(); break;
            case Key.Escape when TagsPanel.Visibility == Visibility.Visible: CloseTags(); break;
            case Key.Escape when immersive: ToggleImmersive(); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void StopPlayback()
    {
        try { player?.Stop(); } catch { }
        playingMedia?.Dispose();
        playingMedia = null;
        PositionSlider.Value = 0;
        TimeText.Text = "00:00 / 00:00";
        if (PlayPause != null)
            PlayPause.Icon = new SymbolIcon(SymbolRegular.Play24);
    }

    private void DisposePlayer()
    {
        timer.Stop();
        StopPlayback();
        player?.Dispose();
        libVlc?.Dispose();
    }

    private static string NaturalSortKey(string path)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            System.IO.Path.GetFileName(path),
            @"\d+",
            match => match.Value.PadLeft(16, '0'));
    }

    private static bool SameSet(LocalStat left, LocalStat right) =>
        Path.GetFullPath(left.LocalDir)
            .Equals(Path.GetFullPath(right.LocalDir), StringComparison.OrdinalIgnoreCase);

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}
