using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using FileSystem = Microsoft.VisualBasic.FileIO.FileSystem;
using RecycleOption = Microsoft.VisualBasic.FileIO.RecycleOption;
using UICancelOption = Microsoft.VisualBasic.FileIO.UICancelOption;
using UIOption = Microsoft.VisualBasic.FileIO.UIOption;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Wpf.Ui.Controls;
using XiurenDownloader;
using XiurenManager.Controls;

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
    private CancellationTokenSource? mediaLoadCts;
    private CancellationTokenSource? setLoadCts;
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private Media? playingMedia;
    private VlcFramePresenter? framePresenter;
    private bool draggingPosition;
    private bool immersive;
    private bool initialSetLoaded;
    private int imageLoadVersion;
    private int setLoadVersion;
    private WindowState windowStateBeforeImmersive = WindowState.Normal;

    internal LocalStat CurrentSet => set;
    internal event Action<LocalStat>? CurrentSetChanged;

    internal ViewerWindow(
        LocalStat set,
        IReadOnlyList<LocalStat>? context = null,
        ViewerSetNavigationMode navigationMode = ViewerSetNavigationMode.Sequential)
    {
        var availableSets = (context ?? [set])
            .Where(item => CatalogStatuses.CanAttemptOpen(item.Availability))
            .DistinctBy(
                item => string.IsNullOrWhiteSpace(item.SetId)
                    ? item.Category + "|" + item.Model + "|" + item.Title
                    : item.SetId,
                StringComparer.OrdinalIgnoreCase)
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
            CancelMediaLoad();
            CancelSetLoad();
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
            framePresenter = new VlcFramePresenter(VideoFrame);
            framePresenter.Attach(player);
            player.Playing += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Pause24));
            player.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Play24));
            player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => PlayPause.Icon = new SymbolIcon(SymbolRegular.Replay24));
            player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (VideoHost.Visibility == Visibility.Visible)
                    ShowVideoError("视频无法播放，请在统计页检查文件完整性");
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
        var previousSet = set;
        slideshowTimer.Stop();
        CancelMediaLoad();
        CancelSetLoad();
        StopPlayback();
        HideVideoSurface();
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
        _ = LoadMediaAsync(selectLast);

        if (!SameSet(previousSet, set))
            CurrentSetChanged?.Invoke(set);

        if (writeLog)
            state.WriteLog($"切换浏览套图: {set.Model} / {set.Title}");
    }

    private async Task LoadMediaAsync(bool selectLast)
    {
        CancelSetLoad();
        var cancellation = new CancellationTokenSource();
        setLoadCts = cancellation;
        var version = ++setLoadVersion;
        ViewerMessage.Text = "正在读取套图索引";
        ViewerMessage.Visibility = Visibility.Visible;
        try
        {
            var files = await state.Catalog.LoadMediaAsync(set, cancellation.Token);
            if (version != setLoadVersion || cancellation.IsCancellationRequested)
                return;
            foreach (var file in files)
                media.Add(new ViewerMediaRow { Path = file.Path, IsVideo = file.IsVideo });
            if (media.Count == 0)
            {
                ViewerMessage.Text = "这套目录内没有可用的图片或视频";
                return;
            }
            Filmstrip.SelectedIndex = selectLast ? media.Count - 1 : 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != setLoadVersion) return;
            ViewerMessage.Text = ex.Message;
            ViewerMessage.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(setLoadCts, cancellation))
                setLoadCts = null;
            cancellation.Dispose();
        }
    }

    private async void ShowMedia(int index)
    {
        if (index < 0 || index >= media.Count) return;
        var loadToken = BeginMediaLoad();
        slideshowTimer.Stop();
        var item = media[index];
        UpdateCanvasNavigationVisibility(item);
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
        HideVideoSurface();
        ImageViewer.Source = null;
        ImageViewer.Visibility = Visibility.Visible;
        try
        {
            var source = await MediaCoverService.LoadViewerImageAsync(
                item.Path,
                state.Settings,
                loadToken);
            if (version != imageLoadVersion) return;
            ImageViewer.Source = source;
            ViewerMessage.Visibility = Visibility.Collapsed;
            RestartSlideshow();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version != imageLoadVersion) return;
            ViewerMessage.Text = "图片无法显示：" + ex.Message;
            RestartSlideshow();
        }
    }

    private CancellationToken BeginMediaLoad()
    {
        CancelMediaLoad();
        mediaLoadCts = new CancellationTokenSource();
        return mediaLoadCts.Token;
    }

    private void CancelMediaLoad()
    {
        var previous = mediaLoadCts;
        mediaLoadCts = null;
        if (previous == null) return;
        previous.Cancel();
        previous.Dispose();
    }

    private void CancelSetLoad()
    {
        setLoadVersion++;
        var previous = setLoadCts;
        setLoadCts = null;
        if (previous == null) return;
        previous.Cancel();
    }

    private void ShowVideo(string path)
    {
        imageLoadVersion++;
        StopPlayback();
        ImageViewer.Source = null;
        ImageViewer.Visibility = Visibility.Collapsed;
        VideoHost.Visibility = Visibility.Visible;
        if (libVlc == null || player == null)
        {
            ShowVideoError("视频播放器未初始化");
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
            ShowVideoError("视频无法播放：" + ex.Message);
        }
    }

    private void ShowVideoError(string message)
    {
        StopPlayback();
        HideVideoSurface();
        ImageViewer.Source = null;
        ImageViewer.Visibility = Visibility.Collapsed;
        ViewerMessage.Text = message;
        ViewerMessage.Visibility = Visibility.Visible;
    }

    private void HideVideoSurface()
    {
        framePresenter?.ClearFrame();
        VideoHost.Visibility = Visibility.Collapsed;
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
        return CatalogStatuses.CanAttemptOpen(candidate.Availability) &&
               candidate.ImageCount + candidate.VideoCount + candidate.InvalidVideoCount > 0;
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
        state.Metadata.QueueSync(set);
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

        TagsColumn.Width = new GridLength(418);
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
        TagsColumn.Width = new GridLength(0);
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
        state.Metadata.QueueSync(set);
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

    private void ViewerHeader_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
        var current = e.OriginalSource as DependencyObject;
        while (current != null && current != ViewerHeader)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase or
                System.Windows.Controls.Slider or
                System.Windows.Controls.Primitives.ToggleButton)
            {
                return;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException) { }
    }

    private void OpenSetFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(set.LocalDir)) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(set.LocalDir);
        Process.Start(start);
    }

    private void MediaActions_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void DeleteCurrentMedia_OnClick(object sender, RoutedEventArgs e)
    {
        if (Filmstrip.SelectedItem is not ViewerMediaRow item || !File.Exists(item.Path)) return;
        if (System.Windows.MessageBox.Show(
                this,
                $"确定删除当前{(item.IsVideo ? "视频" : "图片")}吗？\n\n{item.Name}\n\n文件将被送入回收站。",
                "删除当前媒体",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var index = Filmstrip.SelectedIndex;
        PrepareCurrentMediaForDeletion();
        MediaActionsButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                using var operationLease = ResourceOperationLock.TryAcquire() ??
                    throw new InvalidOperationException(
                        "下载、扫描或存储迁移正在使用资源库，请稍后重试。");
                FileSystem.DeleteFile(
                    item.Path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
                state.Catalog.MarkMediaDeleted(set, item.Path);
            });

            media.Remove(item);
            state.Metadata.QueueSync(set);
            state.WriteLog($"已从查看器删除媒体: {item.Path}");
            state.NotifyDataChanged();

            if (media.Count == 0)
            {
                MediaTitle.Text = "0 / 0";
                ViewerMessage.Text = "这套目录内已经没有图片或视频";
                ViewerMessage.Visibility = Visibility.Visible;
            }
            else
            {
                Filmstrip.SelectedIndex = Math.Min(index, media.Count - 1);
                Filmstrip.ScrollIntoView(Filmstrip.SelectedItem);
            }
        }
        catch (Exception ex)
        {
            state.WriteLog($"查看器删除媒体失败: {item.Path} | {ex.Message}");
            System.Windows.MessageBox.Show(
                this,
                ErrorText.Format(ex),
                "删除失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            if (index >= 0 && index < media.Count)
                ShowMedia(index);
        }
        finally
        {
            MediaActionsButton.IsEnabled = true;
        }
    }

    private async void DeleteCurrentSet_OnClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(set.LocalDir)) return;
        if (System.Windows.MessageBox.Show(
                this,
                $"确定删除整套写真吗？\n\n{set.Title}\n\n整个目录将被送入回收站。",
                "删除整套写真",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        var path = set.LocalDir;
        PrepareCurrentMediaForDeletion();
        MediaActionsButton.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                using var operationLease = ResourceOperationLock.TryAcquire() ??
                    throw new InvalidOperationException(
                        "下载、扫描或存储迁移正在使用资源库，请稍后重试。");
                FileSystem.DeleteDirectory(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
                state.Catalog.MarkUnavailable(
                    set,
                    CatalogStatuses.Deleted,
                    "用户已从查看器将整套资源移入回收站");
            });
            state.WriteLog($"已从查看器删除整套写真: {path}");
            state.NotifyDataChanged();
            Close();
        }
        catch (Exception ex)
        {
            state.WriteLog($"查看器删除套图失败: {path} | {ex.Message}");
            System.Windows.MessageBox.Show(
                this,
                ErrorText.Format(ex),
                "删除失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            MediaActionsButton.IsEnabled = true;
            if (Filmstrip.SelectedIndex >= 0)
                ShowMedia(Filmstrip.SelectedIndex);
        }
    }

    private void PrepareCurrentMediaForDeletion()
    {
        slideshowTimer.Stop();
        CancelMediaLoad();
        imageLoadVersion++;
        StopPlayback();
        HideVideoSurface();
        ImageViewer.Source = null;
    }

    private void Immersive_OnClick(object sender, RoutedEventArgs e) => ToggleImmersive();

    private void ToggleImmersive()
    {
        if (!immersive)
        {
            CloseTags();
            windowStateBeforeImmersive = WindowState;
        }
        immersive = !immersive;
        HeaderRow.Height = immersive ? new GridLength(42) : new GridLength(54);
        FilmstripRow.Height = immersive ? new GridLength(0) : new GridLength(88);
        ControlsRow.Height = immersive ? new GridLength(0) : new GridLength(58);
        ViewerHeader.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        ImmersiveHeader.Visibility = immersive ? Visibility.Visible : Visibility.Collapsed;
        Filmstrip.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        PlaybackControls.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        UpdateCanvasNavigationVisibility(Filmstrip.SelectedItem as ViewerMediaRow);
        WindowState = immersive ? WindowState.Maximized : windowStateBeforeImmersive;
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            ViewerRoot.Focus();
            Keyboard.Focus(ViewerRoot);
        }, DispatcherPriority.Input);
    }

    private void UpdateCanvasNavigationVisibility(ViewerMediaRow? item)
    {
        var visibility = !immersive && item != null
            ? Visibility.Visible
            : Visibility.Collapsed;
        CanvasPrevious.Visibility = visibility;
        CanvasNext.Visibility = visibility;
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
        framePresenter?.Dispose();
        libVlc?.Dispose();
    }

    private static string NaturalSortKey(string path)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            System.IO.Path.GetFileName(path),
            @"\d+",
            match => match.Value.PadLeft(16, '0'));
    }

    private static bool SameSet(LocalStat left, LocalStat right)
    {
        if (!string.IsNullOrWhiteSpace(left.SetId) &&
            !string.IsNullOrWhiteSpace(right.SetId))
            return left.SetId.Equals(right.SetId, StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(left.LocalDir)
            .Equals(Path.GetFullPath(right.LocalDir), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }
}
