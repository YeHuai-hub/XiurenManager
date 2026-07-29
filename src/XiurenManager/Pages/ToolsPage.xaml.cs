using System.Windows;
using System.Windows.Controls;

namespace XiurenManager.Pages;

public partial class ToolsPage : Page
{
    private readonly AppState state = App.State;

    public ToolsPage() => InitializeComponent();

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
