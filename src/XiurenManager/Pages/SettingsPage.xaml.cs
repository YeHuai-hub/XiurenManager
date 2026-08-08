using System.Windows;
using System.Windows.Controls;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class SettingsPage : Page
{
    private readonly AppState state = App.State;
    private Window? hostWindow;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadValues();
        hostWindow = Window.GetWindow(this);
        if (hostWindow == null) return;
        hostWindow.SizeChanged += HostWindow_OnSizeChanged;
        UpdateScrollerHeight();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (hostWindow != null)
            hostWindow.SizeChanged -= HostWindow_OnSizeChanged;
        hostWindow = null;
    }

    private void HostWindow_OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollerHeight();

    private void UpdateScrollerHeight()
    {
        if (hostWindow == null) return;
        SettingsScroller.Height = Math.Max(400, hostWindow.ActualHeight - 48);
    }

    private void SettingsPage_OnPreviewMouseWheel(
        object sender,
        System.Windows.Input.MouseWheelEventArgs e)
    {
        SettingsScroller.ScrollToVerticalOffset(
            Math.Clamp(
                SettingsScroller.VerticalOffset - e.Delta,
                0,
                SettingsScroller.ScrollableHeight));
        e.Handled = true;
    }

    private void LoadValues()
    {
        var settings = state.Settings;
        BaseUrl.Text = settings.BaseUrl;
        UserName.Text = settings.UserName;
        Password.Password = settings.Password;
        CategoryPath.Text = settings.CategoryPath;
        ProxyUrl.Text = settings.ProxyUrl;
        DownloadRoot.Text = settings.DownloadRoot;
        ArchiveRoot.Text = settings.ArchiveRoot;
        BaiduPcs.Text = settings.BaiduPcsPath;
        SevenZip.Text = settings.SevenZipPath;
        Ffprobe.Text = settings.FfprobePath;
        Parallelism.Text = settings.DownloadParallelism.ToString();
        SingleParallelism.Text = settings.SingleFileParallelism.ToString();
        LowSpeedThreshold.Text = settings.LowSpeedThresholdKBps.ToString();
        LowSpeedSeconds.Text = settings.LowSpeedSeconds.ToString();
        LogRetentionDays.Text = settings.LogRetentionDays.ToString();
        LogMaxTotalMB.Text = settings.LogMaxTotalMB.ToString();
        DeleteArchive.IsChecked = settings.DeleteArchiveAfterExtract;
        SkipCompleted.IsChecked = settings.SkipCompleted;
        KeepSidecars.IsChecked = settings.KeepSidecarFiles;
        UseProxy.IsChecked = settings.UseSystemProxy;
        LowSpeedGuard.IsChecked = settings.LowSpeedGuardEnabled;
        LocalHotBudget.Text = settings.LocalHotBudgetGB.ToString();
        LocalReserve.Text = settings.LocalReserveGB.ToString();
        ArchiveReserve.Text = settings.ArchiveReserveGB.ToString();
        MigrationBatch.Text = settings.MigrationBatchGB.ToString();
        StorageCheckMinutes.Text = settings.StorageCheckMinutes.ToString();
        MigrationParallelism.Text = settings.MigrationParallelism.ToString();
        PinnedLocalModels.Text = string.Join("、", settings.PinnedLocalModels);
        StorageManagementEnabled.IsChecked = settings.StorageManagementEnabled;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning)
        {
            MessageBox.Show(
                "请先停止当前下载任务，再修改资源库路径或下载设置。",
                "任务正在运行");
            return;
        }

        var libraryRoot = DownloadRoot.Text.Trim();
        var archiveRoot = ArchiveRoot.Text.Trim();
        try
        {
            var fullRoot = Path.GetFullPath(libraryRoot).TrimEnd('\\');
            var volumeRoot = Path.GetPathRoot(fullRoot)?.TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(fullRoot) ||
                fullRoot.Equals(volumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "资源库不能直接设置为磁盘根目录，请使用类似 F:\\资源 的独立目录。",
                    "资源库根目录");
                return;
            }
            libraryRoot = fullRoot;
        }
        catch (Exception ex)
        {
            MessageBox.Show("资源库路径无效: " + ex.Message, "资源库根目录");
            return;
        }

        try
        {
            archiveRoot = string.IsNullOrWhiteSpace(archiveRoot)
                ? ""
                : Path.GetFullPath(archiveRoot).TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(archiveRoot) &&
                (LibraryPaths.IsInside(libraryRoot, archiveRoot) ||
                 LibraryPaths.IsInside(archiveRoot, libraryRoot) ||
                 archiveRoot.Equals(libraryRoot, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("本地资源库与 NAS 资源库不能相同或互相包含。", "存储路径");
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("NAS 资源库路径无效: " + ex.Message, "存储路径");
            return;
        }
        var settings = state.Settings;
        settings.BaseUrl = BaseUrl.Text.Trim().TrimEnd('/');
        settings.UserName = UserName.Text.Trim();
        settings.Password = Password.Password;
        settings.CategoryPath = string.IsNullOrWhiteSpace(CategoryPath.Text) ? "/tbgx" : CategoryPath.Text.Trim();
        settings.ProxyUrl = NormalizeProxyUrl(ProxyUrl.Text);
        settings.DownloadRoot = libraryRoot;
        settings.ArchiveRoot = archiveRoot;
        settings.BaiduPcsPath = BaiduPcs.Text.Trim();
        settings.SevenZipPath = SevenZip.Text.Trim();
        settings.FfprobePath = Ffprobe.Text.Trim();
        settings.DownloadParallelism = Number(Parallelism.Text, 2, 1, 5);
        settings.SingleFileParallelism = Number(SingleParallelism.Text, 10, 1, 20);
        settings.LowSpeedThresholdKBps = Number(LowSpeedThreshold.Text, 512, 1, 102400);
        settings.LowSpeedSeconds = Number(LowSpeedSeconds.Text, 180, 10, 3600);
        settings.LogRetentionDays = Number(LogRetentionDays.Text, 30, 1, 3650);
        settings.LogMaxTotalMB = Number(LogMaxTotalMB.Text, 100, 10, 10240);
        settings.DeleteArchiveAfterExtract = DeleteArchive.IsChecked == true;
        settings.SkipCompleted = SkipCompleted.IsChecked == true;
        settings.KeepSidecarFiles = KeepSidecars.IsChecked == true;
        settings.UseSystemProxy = UseProxy.IsChecked == true;
        settings.LowSpeedGuardEnabled = LowSpeedGuard.IsChecked == true;
        settings.LocalHotBudgetGB = Number(LocalHotBudget.Text, 600, 50, 3500);
        settings.LocalReserveGB = Number(LocalReserve.Text, 400, 50, 2000);
        settings.ArchiveReserveGB = Number(ArchiveReserve.Text, 450, 50, 2000);
        settings.MigrationBatchGB = Number(MigrationBatch.Text, 30, 1, 200);
        settings.StorageCheckMinutes = Number(StorageCheckMinutes.Text, 15, 2, 1440);
        settings.MigrationParallelism = Number(MigrationParallelism.Text, 3, 1, 8);
        settings.PinnedLocalModels = PinnedLocalModels.Text
            .Split([',', '，', ';', '；', '、', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => XiurenClient.Safe(x.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        settings.StorageManagementEnabled = StorageManagementEnabled.IsChecked == true;
        Directory.CreateDirectory(settings.DownloadRoot);
        foreach (var category in LibraryPaths.Categories(settings))
            Directory.CreateDirectory(Path.Combine(settings.DownloadRoot, category));
        settings.Save();
        if (settings.StorageManagementEnabled)
            state.Storage.Resume();
        else
            state.Storage.Pause();
        state.WriteLog("设置已保存。");
        MessageBox.Show("设置已保存。", "写真资源管理器");
    }

    private static int Number(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;

    private static string NormalizeProxyUrl(string text)
    {
        var value = text.Trim();
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri.TrimEnd('/')
            : "";
    }
}
