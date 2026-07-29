using System.Collections.Concurrent;
using XiurenDownloader;

namespace XiurenManager;

internal static class MediaMaintenanceService
{
    public static (int Files, long Bytes) CleanNonMedia(AppState state)
    {
        var keep = state.Settings.ImageExts
            .Concat(state.Settings.VideoExts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(state.Settings.DownloadRoot, "*", SearchOption.AllDirectories)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            if (keep.Contains(Path.GetExtension(file))) continue;
            try
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                info.Delete();
                deleted++;
            }
            catch (Exception ex)
            {
                state.WriteLog($"无法删除: {file} | {ex.Message}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     state.Settings.DownloadRoot, "*", SearchOption.AllDirectories)
                 .Where(x => !AppPaths.IsInsideTool(x))
                 .OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch { }
        }
        return (deleted, bytes);
    }

    public static async Task<(int Valid, int Invalid)> CheckVideosAsync(
        AppState state,
        CancellationToken token)
    {
        var files = Directory.EnumerateFiles(state.Settings.DownloadRoot, "*", SearchOption.AllDirectories)
            .Where(x => !AppPaths.IsInsideTool(x))
            .Where(x => state.Settings.VideoExts.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var results = new ConcurrentBag<VideoValidationResult>();
        var completed = 0;
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            (file, ct) =>
            {
                results.Add(VideoValidator.Check(file, state.Settings, sampleFrames: true, ct));
                var value = Interlocked.Increment(ref completed);
                if (value % 25 == 0 || value == files.Length)
                    state.WriteLog($"视频检查进度: {value}/{files.Length}");
                return ValueTask.CompletedTask;
            });

        foreach (var group in results
                     .Select(x => new { Result = x, Directory = SetDirectory(state.Settings.DownloadRoot, x.Path) })
                     .Where(x => !string.IsNullOrWhiteSpace(x.Directory))
                     .GroupBy(x => x.Directory, StringComparer.OrdinalIgnoreCase))
        {
            VideoValidator.ClearInvalidMarker(group.Key);
            var invalid = group.Where(x => !x.Result.IsValid).Select(x => x.Result).ToArray();
            if (invalid.Length > 0) VideoValidator.WriteInvalidMarker(group.Key, invalid);
        }
        var invalidCount = results.Count(x => !x.IsValid);
        return (results.Count - invalidCount, invalidCount);
    }

    private static string SetDirectory(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 2 ? Path.Combine(root, parts[0], parts[1]) : "";
    }
}
