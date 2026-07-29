using System.Windows;
using System.Windows.Controls;

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
        DownloadRoot.Text = settings.DownloadRoot;
        BaiduPcs.Text = settings.BaiduPcsPath;
        SevenZip.Text = settings.SevenZipPath;
        Ffprobe.Text = settings.FfprobePath;
        Parallelism.Text = settings.DownloadParallelism.ToString();
        SingleParallelism.Text = settings.SingleFileParallelism.ToString();
        LowSpeedThreshold.Text = settings.LowSpeedThresholdKBps.ToString();
        LowSpeedSeconds.Text = settings.LowSpeedSeconds.ToString();
        DeleteArchive.IsChecked = settings.DeleteArchiveAfterExtract;
        SkipCompleted.IsChecked = settings.SkipCompleted;
        KeepSidecars.IsChecked = settings.KeepSidecarFiles;
        UseProxy.IsChecked = settings.UseSystemProxy;
        LowSpeedGuard.IsChecked = settings.LowSpeedGuardEnabled;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var settings = state.Settings;
        settings.BaseUrl = BaseUrl.Text.Trim().TrimEnd('/');
        settings.UserName = UserName.Text.Trim();
        settings.Password = Password.Password;
        settings.CategoryPath = string.IsNullOrWhiteSpace(CategoryPath.Text) ? "/tbgx" : CategoryPath.Text.Trim();
        settings.DownloadRoot = DownloadRoot.Text.Trim();
        settings.BaiduPcsPath = BaiduPcs.Text.Trim();
        settings.SevenZipPath = SevenZip.Text.Trim();
        settings.FfprobePath = Ffprobe.Text.Trim();
        settings.DownloadParallelism = Number(Parallelism.Text, 2, 1, 5);
        settings.SingleFileParallelism = Number(SingleParallelism.Text, 10, 1, 20);
        settings.LowSpeedThresholdKBps = Number(LowSpeedThreshold.Text, 512, 1, 102400);
        settings.LowSpeedSeconds = Number(LowSpeedSeconds.Text, 180, 10, 3600);
        settings.DeleteArchiveAfterExtract = DeleteArchive.IsChecked == true;
        settings.SkipCompleted = SkipCompleted.IsChecked == true;
        settings.KeepSidecarFiles = KeepSidecars.IsChecked == true;
        settings.UseSystemProxy = UseProxy.IsChecked == true;
        settings.LowSpeedGuardEnabled = LowSpeedGuard.IsChecked == true;
        settings.Save();
        state.WriteLog("设置已保存。");
        MessageBox.Show("设置已保存。", "写真资源管理器");
    }

    private static int Number(string text, int fallback, int min, int max) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, min, max) : fallback;
}
