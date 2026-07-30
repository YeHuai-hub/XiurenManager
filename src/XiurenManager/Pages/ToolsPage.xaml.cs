using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Text.Json;
using XiurenDownloader;

namespace XiurenManager.Pages;

public partial class ToolsPage : Page
{
    private readonly AppState state = App.State;
    private const string MigrationTaskName = "XiurenManager-ResourceMigration";
    private static string MigrationStateFile =>
        Path.Combine(AppPaths.DataDir, "library-migration-state.json");
    private static string MigrationLogFile =>
        Path.Combine(AppPaths.LogDir, "library-migration.log");

    public ToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshMigrationStatus();
    }

    private void RefreshMigration_OnClick(object sender, RoutedEventArgs e) =>
        RefreshMigrationStatus();

    private async void PauseMigration_OnClick(object sender, RoutedEventArgs e)
    {
        await ChangeMigrationTaskAsync("/Change /TN \"" + MigrationTaskName + "\" /DISABLE");
    }

    private async void ResumeMigration_OnClick(object sender, RoutedEventArgs e)
    {
        if (await ChangeMigrationTaskAsync(
                "/Change /TN \"" + MigrationTaskName + "\" /ENABLE"))
        {
            await ChangeMigrationTaskAsync("/Run /TN \"" + MigrationTaskName + "\"");
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

    private async Task<bool> ChangeMigrationTaskAsync(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
                throw new InvalidOperationException("无法启动任务计划程序。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "找不到迁移定时任务。"
                        : error.Trim());
            }
            RefreshMigrationStatus();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "迁移任务");
            return false;
        }
    }

    private void RefreshMigrationStatus()
    {
        if (!File.Exists(MigrationStateFile))
        {
            MigrationStatus.Text = "尚未启动迁移任务";
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(MigrationStateFile));
            var root = document.RootElement;
            var status = root.GetProperty("Status").GetString() ?? "Pending";
            var total = root.GetProperty("TotalMoved").GetInt32();
            var last = root.GetProperty("LastMoved").GetInt32();
            var remaining = root.GetProperty("Remaining").GetInt32();
            var error = root.GetProperty("LastError").GetString() ?? "";
            MigrationStatus.Text =
                $"状态 {status} · 累计 {total} 套 · 本轮 {last} 套 · 剩余 {remaining} 套" +
                (string.IsNullOrWhiteSpace(error) ? "" : $" · {error}");
        }
        catch (Exception ex)
        {
            MigrationStatus.Text = "进度读取失败: " + ex.Message;
        }
    }

    private async void Scan_OnClick(object sender, RoutedEventArgs e)
    {
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
