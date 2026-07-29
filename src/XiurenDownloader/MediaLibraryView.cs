using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace XiurenDownloader;

internal sealed class LibrarySetRow
{
    [Browsable(false)]
    public LocalStat Item { get; init; } = new();
    public string Model => Item.Model;
    public string Title => Item.Title;
    public int Score { get; init; }
    public int MediaCount => Item.ImageCount + Item.VideoCount + Item.InvalidVideoCount;
    public string Size => MediaLibraryView.FormatBytes(Item.TotalBytes);
}

internal sealed class MediaFileRow
{
    public string Path { get; init; } = "";
    public bool IsVideo { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Type => IsVideo ? "视频" : "图片";
    public string Size
    {
        get
        {
            try { return MediaLibraryView.FormatBytes(new FileInfo(Path).Length); }
            catch { return ""; }
        }
    }
}

internal sealed class MediaLibraryView : UserControl
{
    private readonly Database db;
    private readonly Settings settings;
    private readonly FavoriteStore favorites = FavoriteStore.Load();
    private readonly Action<string> writeLog;
    private readonly DataGridView setGrid = new ResponsiveDataGridView { AccessibleName = "本地套图列表" };
    private readonly ListView mediaList = new();
    private readonly TextBox filter = new();
    private readonly ComboBox scope = new();
    private readonly ComboBox sort = new();
    private readonly Label title = new();
    private readonly Label score = new();
    private readonly Label empty = new();
    private readonly PictureBox picture = new();
    private readonly VideoView video = new();
    private readonly Button playPause = new();
    private readonly TrackBar position = new();
    private readonly TrackBar volume = new();
    private readonly Label time = new();
    private readonly System.Windows.Forms.Timer playbackTimer = new() { Interval = 300 };
    private readonly List<MediaFileRow> currentMedia = [];
    private BindingList<LibrarySetRow> rows = [];
    private LocalStat? currentSet;
    private int currentIndex = -1;
    private bool draggingPosition;
    private bool changingMediaSelection;
    private int imageLoadVersion;
    private LibVLC? libVlc;
    private MediaPlayer? player;
    private Media? playingMedia;

    public MediaLibraryView(Database db, Settings settings, Action<string> writeLog)
    {
        this.db = db;
        this.settings = settings;
        this.writeLog = writeLog;
        Dock = DockStyle.Fill;
        BackColor = ModernTheme.Background;
        BuildUi();
        InitializePlayer();
        RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        var selectedPath = currentSet?.LocalDir ?? "";
        IEnumerable<LocalStat> values = db.LocalFiles.Where(x => Directory.Exists(x.LocalDir));
        var text = filter.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            values = values.Where(x =>
                x.Model.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                x.Title.Contains(text, StringComparison.OrdinalIgnoreCase));

        var projected = values.Select(x => new LibrarySetRow
        {
            Item = x,
            Score = favorites.GetScore(x)
        });

        if (scope.SelectedIndex == 1)
            projected = projected.Where(x => x.Score > 0);

        projected = sort.SelectedIndex switch
        {
            1 => projected.OrderBy(x => x.Model).ThenBy(x => x.Title),
            2 => projected.OrderByDescending(x => x.Item.LastScanned).ThenBy(x => x.Model),
            _ => projected.OrderByDescending(x => x.Score).ThenBy(x => x.Model).ThenBy(x => x.Title)
        };

        rows = new BindingList<LibrarySetRow>(projected.ToList());
        setGrid.DataSource = rows;
        if (rows.Count == 0)
        {
            ClearSet();
            return;
        }

        var index = rows.ToList().FindIndex(x =>
            x.Item.LocalDir.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        setGrid.ClearSelection();
        setGrid.Rows[index].Selected = true;
        setGrid.CurrentCell = setGrid.Rows[index].Cells[0];
        LoadSelectedSet();
    }

    public void OpenSet(LocalStat item)
    {
        filter.Clear();
        scope.SelectedIndex = 0;
        RefreshLibrary();
        var index = rows.ToList().FindIndex(x =>
            x.Item.LocalDir.Equals(item.LocalDir, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        setGrid.ClearSelection();
        setGrid.Rows[index].Selected = true;
        setGrid.CurrentCell = setGrid.Rows[index].Cells[0];
        LoadSelectedSet();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            playbackTimer.Stop();
            StopPlayback();
            player?.Dispose();
            libVlc?.Dispose();
            picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var commandBar = new Panel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(12, 10, 12, 8), BackColor = ModernTheme.Surface };
        Controls.Add(commandBar);
        var commandFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = ModernTheme.Surface };
        commandBar.Controls.Add(commandFlow);
        commandFlow.Controls.Add(new Label { Text = "筛选", AutoSize = true, Margin = new Padding(0, 7, 8, 0), ForeColor = ModernTheme.Muted });
        filter.Width = 220;
        filter.PlaceholderText = "模特或套图名称";
        filter.Margin = new Padding(0, 2, 12, 0);
        filter.TextChanged += (_, _) => RefreshLibrary();
        commandFlow.Controls.Add(filter);
        scope.DropDownStyle = ComboBoxStyle.DropDownList;
        scope.Width = 110;
        scope.Items.AddRange(["全部套图", "喜欢合集"]);
        scope.SelectedIndex = 0;
        scope.Margin = new Padding(0, 2, 8, 0);
        scope.SelectedIndexChanged += (_, _) => RefreshLibrary();
        commandFlow.Controls.Add(scope);
        sort.DropDownStyle = ComboBoxStyle.DropDownList;
        sort.Width = 125;
        sort.Items.AddRange(["喜爱值优先", "按名称排序", "最近扫描"]);
        sort.SelectedIndex = 0;
        sort.Margin = new Padding(0, 2, 8, 0);
        sort.SelectedIndexChanged += (_, _) => RefreshLibrary();
        commandFlow.Controls.Add(sort);
        var refresh = MakeButton("刷新媒体库", 105);
        refresh.Click += (_, _) => RefreshLibrary();
        commandFlow.Controls.Add(refresh);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 385,
            FixedPanel = FixedPanel.Panel1,
            BackColor = ModernTheme.Border
        };
        split.HandleCreated += (_, _) =>
        {
            split.Panel1MinSize = 280;
            split.Panel2MinSize = 420;
            if (split.Width > 900)
                split.SplitterDistance = 385;
        };
        Controls.Add(split);
        split.BringToFront();

        ConfigureSetGrid();
        split.Panel1.Controls.Add(setGrid);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = ModernTheme.Surface,
            ColumnCount = 1,
            RowCount = 4
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        split.Panel2.Controls.Add(right);

        var setHeader = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 12, 6), BackColor = ModernTheme.Surface };
        title.Dock = DockStyle.Fill;
        title.AutoEllipsis = true;
        title.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold);
        title.ForeColor = ModernTheme.Ink;
        title.TextAlign = ContentAlignment.MiddleLeft;
        score.Dock = DockStyle.Right;
        score.Width = 115;
        score.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        score.ForeColor = ModernTheme.Score;
        score.TextAlign = ContentAlignment.MiddleRight;
        setHeader.Controls.Add(title);
        setHeader.Controls.Add(score);
        right.Controls.Add(setHeader, 0, 0);

        var canvas = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Canvas };
        picture.Dock = DockStyle.Fill;
        picture.BackColor = ModernTheme.Canvas;
        picture.SizeMode = PictureBoxSizeMode.Zoom;
        video.Dock = DockStyle.Fill;
        video.BackColor = ModernTheme.Canvas;
        video.Visible = false;
        empty.Dock = DockStyle.Fill;
        empty.Text = "请选择一套本地写真";
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Color.FromArgb(184, 194, 201);
        empty.Font = new Font("Microsoft YaHei UI", 11);
        canvas.Controls.Add(picture);
        canvas.Controls.Add(video);
        canvas.Controls.Add(empty);
        right.Controls.Add(canvas, 0, 1);

        ConfigureMediaList();
        right.Controls.Add(mediaList, 0, 2);

        var playback = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(10, 10, 8, 7),
            BackColor = Color.FromArgb(235, 240, 242)
        };
        var previous = MakeButton("上一项", 72);
        previous.Click += (_, _) => ShowRelative(-1);
        playPause.Text = "播放";
        playPause.Size = new Size(72, 34);
        ModernTheme.StyleButton(playPause, accent: true);
        ModernTheme.RoundButton(playPause);
        playPause.Click += (_, _) => TogglePlayback();
        var next = MakeButton("下一项", 72);
        next.Click += (_, _) => ShowRelative(1);
        playback.Controls.Add(previous);
        playback.Controls.Add(playPause);
        playback.Controls.Add(next);

        position.Minimum = 0;
        position.Maximum = 1000;
        position.Width = 215;
        position.TickStyle = TickStyle.None;
        position.Margin = new Padding(8, 4, 2, 0);
        position.MouseDown += (_, _) => draggingPosition = true;
        position.MouseUp += (_, _) =>
        {
            if (player is { Length: > 0 })
                player.Time = (long)(player.Length * (position.Value / 1000d));
            draggingPosition = false;
        };
        playback.Controls.Add(position);
        time.Text = "00:00 / 00:00";
        time.AutoSize = false;
        time.Size = new Size(105, 34);
        time.TextAlign = ContentAlignment.MiddleCenter;
        time.ForeColor = ModernTheme.Muted;
        playback.Controls.Add(time);

        var volumeLabel = new Label { Text = "音量", AutoSize = true, Margin = new Padding(4, 9, 0, 0), ForeColor = ModernTheme.Muted };
        playback.Controls.Add(volumeLabel);
        volume.Minimum = 0;
        volume.Maximum = 100;
        volume.Value = 80;
        volume.Width = 78;
        volume.TickStyle = TickStyle.None;
        volume.Margin = new Padding(0, 4, 8, 0);
        volume.ValueChanged += (_, _) => { if (player != null) player.Volume = volume.Value; };
        playback.Controls.Add(volume);

        var minus = MakeButton("撤销 1", 72);
        minus.Click += (_, _) => ChangeScore(-1);
        var plus = MakeButton("看完本套  +1", 125, scoreButton: true);
        plus.Click += (_, _) => ChangeScore(1);
        playback.Controls.Add(minus);
        playback.Controls.Add(plus);
        right.Controls.Add(playback, 0, 3);

        playbackTimer.Tick += (_, _) => UpdatePlaybackUi();
        playbackTimer.Start();
    }

    private void ConfigureSetGrid()
    {
        setGrid.Dock = DockStyle.Fill;
        setGrid.ReadOnly = true;
        setGrid.AllowUserToAddRows = false;
        setGrid.AllowUserToDeleteRows = false;
        setGrid.AllowUserToResizeRows = false;
        setGrid.AutoGenerateColumns = false;
        setGrid.MultiSelect = false;
        setGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        setGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LibrarySetRow.Model), HeaderText = "模特", Width = 90 });
        setGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LibrarySetRow.Title), HeaderText = "套图", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 145 });
        setGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LibrarySetRow.Score), HeaderText = "喜爱", Width = 58 });
        setGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(LibrarySetRow.MediaCount), HeaderText = "媒体", Width = 58 });
        setGrid.SelectionChanged += (_, _) => LoadSelectedSet();
        ModernTheme.StyleGrid(setGrid);
    }

    private void ConfigureMediaList()
    {
        mediaList.Dock = DockStyle.Fill;
        mediaList.View = View.Details;
        mediaList.FullRowSelect = true;
        mediaList.HideSelection = false;
        mediaList.MultiSelect = false;
        mediaList.BorderStyle = BorderStyle.None;
        mediaList.BackColor = Color.FromArgb(248, 250, 251);
        mediaList.ForeColor = ModernTheme.Ink;
        mediaList.Columns.Add("文件", 520);
        mediaList.Columns.Add("类型", 70);
        mediaList.Columns.Add("大小", 90);
        mediaList.SelectedIndexChanged += (_, _) =>
        {
            if (changingMediaSelection) return;
            if (mediaList.SelectedItems.Count == 0) return;
            ShowMedia(mediaList.SelectedItems[0].Index);
        };
    }

    private void InitializePlayer()
    {
        try
        {
            Core.Initialize();
            libVlc = new LibVLC("--no-video-title-show", "--quiet");
            player = new MediaPlayer(libVlc) { Volume = volume.Value };
            video.MediaPlayer = player;
            player.Playing += (_, _) => Ui(() => playPause.Text = "暂停");
            player.Paused += (_, _) => Ui(() => playPause.Text = "播放");
            player.Stopped += (_, _) => Ui(() => playPause.Text = "播放");
            player.EndReached += (_, _) => Ui(() => playPause.Text = "重播");
            player.EncounteredError += (_, _) => Ui(() =>
            {
                playPause.Text = "播放";
                empty.Text = "这个视频无法播放，请在“统计”页检查文件完整性";
                empty.Visible = true;
                empty.BringToFront();
            });
        }
        catch (Exception ex)
        {
            writeLog("内嵌视频播放器初始化失败: " + ex.Message);
            empty.Text = "视频播放器初始化失败，图片仍可正常浏览";
            empty.Visible = true;
        }
    }

    private void LoadSelectedSet()
    {
        if (setGrid.CurrentRow?.DataBoundItem is not LibrarySetRow row) return;
        if (currentSet?.LocalDir.Equals(row.Item.LocalDir, StringComparison.OrdinalIgnoreCase) == true &&
            currentMedia.Count > 0) return;

        currentSet = row.Item;
        title.Text = row.Item.Title;
        UpdateScore();
        StopPlayback();
        picture.Image?.Dispose();
        picture.Image = null;
        currentMedia.Clear();
        mediaList.Items.Clear();

        if (!Directory.Exists(row.Item.LocalDir))
        {
            empty.Text = "本地目录不存在";
            empty.Visible = true;
            return;
        }

        try
        {
            var imageExts = settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var videoExts = settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(row.Item.LocalDir, "*", SearchOption.AllDirectories)
                         .Where(x => !AppPaths.IsInsideTool(x))
                         .OrderBy(NaturalSortKey, StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(path);
                var isVideo = videoExts.Contains(extension);
                if (!isVideo && !imageExts.Contains(extension)) continue;
                currentMedia.Add(new MediaFileRow { Path = path, IsVideo = isVideo });
            }

            foreach (var item in currentMedia)
            {
                var rowItem = new ListViewItem(item.Name) { Tag = item };
                rowItem.SubItems.Add(item.Type);
                rowItem.SubItems.Add(item.Size);
                mediaList.Items.Add(rowItem);
            }

            if (currentMedia.Count == 0)
            {
                empty.Text = "这套目录内没有可浏览的图片或视频";
                empty.Visible = true;
                return;
            }

            ShowMedia(0);
        }
        catch (Exception ex)
        {
            empty.Text = "读取媒体失败: " + ex.Message;
            empty.Visible = true;
        }
    }

    private void ClearSet()
    {
        currentSet = null;
        currentMedia.Clear();
        currentIndex = -1;
        mediaList.Items.Clear();
        title.Text = "没有符合条件的本地套图";
        score.Text = "";
        StopPlayback();
        picture.Image?.Dispose();
        picture.Image = null;
        empty.Text = "请调整筛选条件，或先在“统计”页扫描本地目录";
        empty.Visible = true;
        empty.BringToFront();
    }

    private void ShowMedia(int index)
    {
        if (index < 0 || index >= currentMedia.Count) return;
        currentIndex = index;
        var item = currentMedia[index];
        if (mediaList.SelectedIndices.Count == 0 || mediaList.SelectedIndices[0] != index)
        {
            changingMediaSelection = true;
            mediaList.SelectedItems.Clear();
            mediaList.Items[index].Selected = true;
            mediaList.Items[index].EnsureVisible();
            changingMediaSelection = false;
        }

        title.Text = currentSet == null
            ? item.Name
            : $"{currentSet.Title}    ·    {index + 1}/{currentMedia.Count}    ·    {item.Name}";
        if (item.IsVideo)
            ShowVideo(item.Path);
        else
            _ = ShowImageAsync(item.Path);
    }

    private async Task ShowImageAsync(string path)
    {
        var version = ++imageLoadVersion;
        StopPlayback();
        video.Visible = false;
        picture.Visible = true;
        picture.BringToFront();
        empty.Text = "正在读取图片…";
        empty.Visible = true;
        empty.BringToFront();
        var old = picture.Image;
        picture.Image = null;
        old?.Dispose();

        try
        {
            var image = await Task.Run(() => LoadImage(path));
            if (version != imageLoadVersion)
            {
                image.Dispose();
                return;
            }
            picture.Image = image;
            picture.BringToFront();
            empty.Visible = false;
        }
        catch (Exception ex)
        {
            if (version != imageLoadVersion) return;
            empty.Text = "图片无法显示: " + ex.Message;
            empty.Visible = true;
            empty.BringToFront();
        }
    }

    private Image LoadImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
        catch
        {
            var converted = ConvertUnsupportedImage(path);
            using var stream = new FileStream(converted, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
    }

    private string ConvertUnsupportedImage(string path)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(settings.FfprobePath) ?? "", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new InvalidDataException("当前图片格式不受系统解码器支持");

        var info = new FileInfo(path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks)));
        var cacheDir = Path.Combine(AppPaths.DataDir, "image-cache");
        Directory.CreateDirectory(cacheDir);
        var output = Path.Combine(cacheDir, key + ".png");
        if (File.Exists(output)) return output;

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-vf");
        start.ArgumentList.Add("scale=4096:-2:force_original_aspect_ratio=decrease");
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动图片解码器");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "图片解码失败" : error.Trim());
        return output;
    }

    private void ShowVideo(string path)
    {
        imageLoadVersion++;
        picture.Image?.Dispose();
        picture.Image = null;
        picture.Visible = false;
        video.Visible = true;
        video.BringToFront();
        empty.Visible = false;
        StopPlayback();

        if (libVlc == null || player == null)
        {
            empty.Text = "视频播放器未能初始化";
            empty.Visible = true;
            empty.BringToFront();
            return;
        }

        try
        {
            playingMedia = new Media(libVlc, path, FromType.FromPath);
            if (!player.Play(playingMedia))
                throw new InvalidOperationException("播放器拒绝打开这个文件");
        }
        catch (Exception ex)
        {
            empty.Text = "视频无法播放: " + ex.Message;
            empty.Visible = true;
            empty.BringToFront();
        }
    }

    private void TogglePlayback()
    {
        if (currentIndex < 0 || currentIndex >= currentMedia.Count || !currentMedia[currentIndex].IsVideo)
            return;
        if (player == null) return;

        if (player.State == VLCState.Ended || player.State == VLCState.Stopped)
            ShowVideo(currentMedia[currentIndex].Path);
        else if (player.IsPlaying)
            player.Pause();
        else
            player.Play();
    }

    private void StopPlayback()
    {
        try { player?.Stop(); } catch { }
        playingMedia?.Dispose();
        playingMedia = null;
        playPause.Text = "播放";
        position.Value = 0;
        time.Text = "00:00 / 00:00";
    }

    private void ShowRelative(int delta)
    {
        if (currentMedia.Count == 0) return;
        var index = Math.Clamp(currentIndex + delta, 0, currentMedia.Count - 1);
        ShowMedia(index);
    }

    private void ChangeScore(int delta)
    {
        if (currentSet == null) return;
        var value = favorites.ChangeScore(currentSet, delta);
        writeLog($"{currentSet.Model} / {currentSet.Title} 喜爱值已改为 {value}");
        RefreshLibrary();
    }

    private void UpdateScore()
    {
        score.Text = currentSet == null ? "" : $"喜爱值  {favorites.GetScore(currentSet)}";
    }

    private void UpdatePlaybackUi()
    {
        if (player is not { Length: > 0 } || draggingPosition) return;
        var value = (int)Math.Clamp(player.Time * 1000d / player.Length, 0, 1000);
        position.Value = value;
        time.Text = $"{FormatTime(player.Time)} / {FormatTime(player.Length)}";
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }

    private static Button MakeButton(string text, int width, bool scoreButton = false)
    {
        var button = new Button { Text = text, Size = new Size(width, 34), Margin = new Padding(3, 0, 3, 0) };
        ModernTheme.StyleButton(button, score: scoreButton);
        ModernTheme.RoundButton(button);
        return button;
    }

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"hh\:mm\:ss") : value.ToString(@"mm\:ss");
    }

    private static string NaturalSortKey(string path)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            System.IO.Path.GetFileName(path),
            @"\d+",
            m => m.Value.PadLeft(16, '0'));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value:0.##} {units[index]}";
    }
}
