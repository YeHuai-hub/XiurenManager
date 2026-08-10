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
        if (state.Queue.IsRunning || state.Storage.IsRunning)
        {
            MessageBox.Show("下载队列或存储迁移正在运行，请先停止或等待完成。");
            return;
        }
        try
        {
            await LocalScanner.ScanExclusiveAsync(state);
            MessageBox.Show("本地资源扫描完成。");
        }
        catch (Exception ex)
        {
            state.WriteLog("资源账本扫描失败: " + ex.Message);
            MessageBox.Show(
                ErrorText.Format(ex),
                "扫描失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void Clean_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning || state.Storage.IsRunning)
        {
            MessageBox.Show("下载队列或存储迁移正在运行，请先停止或等待完成。");
            return;
        }
        if (MessageBox.Show(
                "将删除下载目录中所有非图片、非视频文件，并清除空目录。是否继续？",
                "清理文件",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try
        {
            var result = await Task.Run(() => MediaMaintenanceService.CleanNonMedia(state));
            state.WriteLog($"清理完成: 删除 {result.Files} 个文件，释放 {result.Bytes / 1024d / 1024d:0.##} MB");
            MessageBox.Show($"已删除 {result.Files} 个文件。\n释放 {result.Bytes / 1024d / 1024d:0.##} MB。");
        }
        catch (Exception ex)
        {
            state.WriteLog("清理文件失败: " + ex.Message);
            MessageBox.Show(ex.Message, "清理文件失败");
        }
    }

    private async void CleanEmptyDirectories_OnClick(object sender, RoutedEventArgs e)
    {
        if (state.Queue.IsRunning || state.Storage.IsRunning)
        {
            MessageBox.Show("下载队列或存储迁移正在运行，请先停止或等待完成。");
            return;
        }

        CleanEmptyDirectoriesButton.IsEnabled = false;
        try
        {
            var preview = await Task.Run(() =>
                MediaMaintenanceService.FindEmptySetDirectories(state));
            var local = preview
                .Where(x => x.StorageTier == StorageTiers.Local)
                .ToArray();
            var archive = preview
                .Where(x => x.StorageTier == StorageTiers.Archive)
                .ToArray();
            if (local.Length == 0 && archive.Length == 0)
            {
                MessageBox.Show("没有发现真正的空套图目录。含压缩包、断点文件或其他文件的目录不会列入清理范围。");
                return;
            }

            var deleted = 0;
            var failed = new List<string>();
            if (local.Length > 0 && MessageBox.Show(
                    BuildEmptyDirectoryPreview(
                        "本地",
                        local,
                        $"NAS 另有 {archive.Length} 个空目录，本次不会处理。"),
                    "清理本地空目录",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                var result = await Task.Run(() =>
                    MediaMaintenanceService.DeleteEmptySetDirectories(state, local));
                deleted += result.Deleted;
                failed.AddRange(result.FailedPaths);
            }

            if (archive.Length > 0 && MessageBox.Show(
                    BuildEmptyDirectoryPreview(
                        "NAS",
                        archive,
                        "这是独立确认步骤，不会删除任何含文件的目录。"),
                    "清理 NAS 空目录",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                var result = await Task.Run(() =>
                    MediaMaintenanceService.DeleteEmptySetDirectories(state, archive));
                deleted += result.Deleted;
                failed.AddRange(result.FailedPaths);
            }

            state.WriteLog($"空目录清理完成: 删除 {deleted} 个，失败或跳过 {failed.Count} 个");
            MessageBox.Show($"空目录清理完成。\n删除: {deleted}\n失败或因出现文件而跳过: {failed.Count}");
        }
        catch (Exception ex)
        {
            state.WriteLog("空目录清理失败: " + ex.Message);
            MessageBox.Show(ex.Message, "空目录清理失败");
        }
        finally
        {
            CleanEmptyDirectoriesButton.IsEnabled = true;
        }
    }

    private static string BuildEmptyDirectoryPreview(
        string location,
        IReadOnlyList<EmptySetDirectory> entries,
        string note)
    {
        var paths = string.Join(
            Environment.NewLine,
            entries.Take(8).Select(x => "• " + x.Path));
        var remaining = entries.Count > 8
            ? $"\n……另有 {entries.Count - 8} 个"
            : "";
        return $"发现 {entries.Count} 个{location}空套图目录：\n\n{paths}{remaining}\n\n{note}\n\n" +
               "删除前会再次检查；只要目录内出现任何文件，就会自动跳过。是否继续？";
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
