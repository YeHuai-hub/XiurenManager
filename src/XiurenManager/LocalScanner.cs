using XiurenDownloader;

namespace XiurenManager;

internal static class LocalScanner
{
    public static void Scan(AppState state, bool notify = true)
    {
        var root = state.Settings.DownloadRoot;
        if (!Directory.Exists(root))
        {
            state.WriteLog($"下载目录不存在: {root}");
            return;
        }

        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<LocalStat>();

        foreach (var modelDir in Directory.EnumerateDirectories(root)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            var model = Path.GetFileName(modelDir);
            foreach (var setDir in Directory.EnumerateDirectories(modelDir))
            {
                try
                {
                    var files = Directory.EnumerateFiles(setDir, "*", SearchOption.AllDirectories)
                        .Where(x => !AppPaths.IsInsideTool(x))
                        .Select(x => new FileInfo(x))
                        .ToArray();
                    var videos = files.Where(x => videoExts.Contains(x.Extension)).ToArray();
                    var quickInvalid = videos.Count(x => !VideoValidator.QuickHeaderLooksValid(x.FullName));
                    var invalidVideos = Math.Max(quickInvalid, VideoValidator.MarkedInvalidCount(setDir));
                    results.Add(new LocalStat
                    {
                        Model = model,
                        Title = Path.GetFileName(setDir),
                        LocalDir = setDir,
                        ImageCount = files.Count(x => imageExts.Contains(x.Extension)),
                        VideoCount = Math.Max(0, videos.Length - invalidVideos),
                        InvalidVideoCount = invalidVideos,
                        TotalBytes = files.Sum(x => x.Length),
                        LastScanned = DateTime.Now.ToString("s")
                    });
                }
                catch (Exception ex)
                {
                    state.WriteLog($"扫描失败: {setDir} | {ex.Message}");
                }
            }
        }

        state.Database.LocalFiles = results;
        state.Database.Save();
        state.WriteLog($"本地扫描完成: {results.Count} 套资源");
        if (notify)
            state.NotifyDataChanged();
    }
}
