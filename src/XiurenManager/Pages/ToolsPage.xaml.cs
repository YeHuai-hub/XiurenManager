using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Text.Json;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class ToolsPage : Page
{
    private readonly AppState state = App.State;
    private static string MigrationLogFile =>
        Path.Combine(AppPaths.LogDir, "storage-migration.log");

    public ToolsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        state.Storage.StatusChanged += Storage_OnStatusChanged;
        RefreshMigrationStatus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        state.Storage.StatusChanged -= Storage_OnStatusChanged;

    private void Storage_OnStatusChanged(object? sender, EventArgs e) =>
        RefreshMigrationStatus();

    private void RefreshMigration_OnClick(object sender, RoutedEventArgs e) =>
        RefreshMigrationStatus();

    private void PauseMigration_OnClick(object sender, RoutedEventArgs e)
    {
        state.Storage.Pause();
        RefreshMigrationStatus();
    }

    private void ResumeMigration_OnClick(object sender, RoutedEventArgs e)
    {
        state.Storage.Resume();
        RefreshMigrationStatus();
    }

    private async void RunMigration_OnClick(object sender, RoutedEventArgs e)
    {
        RunMigrationButton.IsEnabled = false;
        try
        {
            await state.Storage.RunBatchAsync(manual: true);
            RefreshMigrationStatus();
        }
        finally
        {
            RunMigrationButton.IsEnabled = true;
        }
    }

    private void OpenMigrationLog_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(MigrationLogFile))
        {
            MessageBox.Show("迁移日志尚未生成。");
            return;
        }
        Process.Start(new ProcessStartInfo("notepad.exe", MigrationLogFile)
        {
            UseShellExecute = true
        });
    }

    private void RefreshMigrationStatus()
    {
        var value = state.Storage.GetStatus();
        var enabled = value.Enabled ? "自动整理已启用" : "自动整理已暂停";
        var current = string.IsNullOrWhiteSpace(value.CurrentModel)
            ? ""
            : $" · 当前 {value.CurrentModel} ({value.Phase})";
        var progress = value.TotalBytes <= 0
            ? ""
            : $" · 进度 {value.CurrentFiles}/{value.TotalFiles}，" +
              $"{StorageMigrationService.FormatBytes(value.CurrentBytes)}/" +
              $"{StorageMigrationService.FormatBytes(value.TotalBytes)} " +
              $"({Math.Clamp(value.CurrentBytes * 100d / value.TotalBytes, 0, 100):0.0}%)";
        MigrationStatus.Text =
            $"{enabled} · 状态 {value.Status}{current} · 本批 {value.LastBatchModels} 个模特 / " +
            StorageMigrationService.FormatBytes(value.LastBatchBytes) +
            progress +
            (string.IsNullOrWhiteSpace(value.LastError) ? "" : " · " + value.LastError);
        MigrationCapacity.Text =
            $"本地可用 {StorageMigrationService.FormatBytes(value.LocalFreeBytes)} · " +
            (value.ArchiveOnline
                ? $"NAS 可用 {StorageMigrationService.FormatBytes(value.ArchiveFreeBytes)}"
                : "NAS 离线") +
            $" · 累计迁移 {value.TotalMovedModels} 个模特 / {StorageMigrationService.FormatBytes(value.TotalMovedBytes)}";
        RunMigrationButton.IsEnabled = !state.Storage.IsRunning;
    }

    private async void Scan_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Storage.IsRunning)
        {
            MessageBox.Show("存储迁移正在运行，请等待当前模特完成或先暂停迁移。");
            return;
        }
        await Task.Run(() => LocalScanner.Scan(state));
        MessageBox.Show("本地资源扫描完成。");
    }

    private async void Clean_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "将删除下载目录中所有非图片、非视频文件，并清除空目录。是否继续？",
                "清理文件",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        var result = await Task.Run(() => MediaMaintenanceService.CleanNonMedia(state));
        await Task.Run(() => LocalScanner.Scan(state));
        state.WriteLog($"清理完成: 删除 {result.Files} 个文件，释放 {result.Bytes / 1024d / 1024d:0.##} MB");
        MessageBox.Show($"已删除 {result.Files} 个文件。\n释放 {result.Bytes / 1024d / 1024d:0.##} MB。");
    }

    private async void CheckVideos_OnClick(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(state.Settings.FfprobePath))
        {
            MessageBox.Show("找不到 FFprobe，请先到设置页确认路径。");
            return;
        }
        CheckVideoButton.IsEnabled = false;
        try
        {
            state.WriteLog("开始检查视频完整性，后台并发 8。");
            var result = await MediaMaintenanceService.CheckVideosAsync(state, CancellationToken.None);
            await Task.Run(() => LocalScanner.Scan(state));
            state.WriteLog($"视频检查完成: 正常 {result.Valid}，损坏 {result.Invalid}");
            MessageBox.Show($"检查完成。\n正常: {result.Valid}\n损坏: {result.Invalid}");
        }
        catch (Exception ex)
        {
            state.WriteLog("视频检查失败: " + ex.Message);
            MessageBox.Show(ex.Message, "视频检查失败");
        }
        finally
        {
            CheckVideoButton.IsEnabled = true;
        }
    }
}
