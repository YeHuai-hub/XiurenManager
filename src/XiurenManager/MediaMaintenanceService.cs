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
        var setDirectories = ExistingSetDirectories(state);
        foreach (var file in setDirectories
                     .SelectMany(x => Directory.EnumerateFiles(
                         x,
                         "*",
                         SearchOption.AllDirectories))
                     .Where(x => !AppPaths.IsInsideTool(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
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

        foreach (var directory in setDirectories
                 .SelectMany(x => Directory.EnumerateDirectories(
                     x,
                     "*",
                     SearchOption.AllDirectories))
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
        var setDirectories = ExistingSetDirectories(state);
        var files = setDirectories
            .SelectMany(x => Directory.EnumerateFiles(x, "*", SearchOption.AllDirectories))
            .Where(x => !AppPaths.IsInsideTool(x))
            .Where(x => state.Settings.VideoExts.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
                     .Select(x => new
                     {
                         Result = x,
                         Directory = FindSetDirectory(setDirectories, x.Path)
                     })
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

    private static string[] ExistingSetDirectories(AppState state)
    {
        return state.Database.LocalFiles
            .Select(x => x.LocalDir)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindSetDirectory(IEnumerable<string> directories, string file)
    {
        var fullFile = Path.GetFullPath(file);
        return directories.FirstOrDefault(directory =>
        {
            var fullDirectory = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }) ?? "";
    }
}
