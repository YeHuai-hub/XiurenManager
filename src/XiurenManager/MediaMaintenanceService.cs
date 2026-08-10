using System.Collections.Concurrent;
using XiurenDownloader;

namespace XiurenManager;

internal sealed record EmptySetDirectory(string Path, string StorageTier);

internal sealed record EmptyDirectoryCleanupResult(
    int Deleted,
    IReadOnlyList<string> FailedPaths);

internal static class MediaMaintenanceService
{
    public static (int Files, long Bytes) CleanNonMedia(AppState state)
    {
        using var operationLease = ResourceOperationLock.TryAcquire() ??
                                   throw new InvalidOperationException(
                                       "下载、迁移或其他清理任务正在使用资源库，请稍后重试。");
        var keep = state.Settings.ImageExts
            .Concat(state.Settings.VideoExts)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        long bytes = 0;
        var changedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setDirectories = ExistingSetDirectories(state);
        foreach (var setDirectory in setDirectories)
        {
            var files = Directory.EnumerateFiles(
                    setDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Where(x => !AppPaths.IsInsideTool(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var hasUsableMedia = files.Any(file => MediaFileValidator.IsUsable(
                file,
                state.Settings.ImageExts,
                state.Settings.VideoExts));
            foreach (var file in files)
            {
                if (!hasUsableMedia ||
                    keep.Contains(Path.GetExtension(file)) ||
                    Path.GetFileName(file).Equals(
                        VideoValidator.InvalidMarkerName,
                        StringComparison.OrdinalIgnoreCase) ||
                    SetMetadataSidecar.IsMetadataFile(file) ||
                    IsIncompleteDownload(file) ||
                    IsArchive(file, state.Settings))
                {
                    continue;
                }
                try
                {
                    var info = new FileInfo(file);
                    bytes += info.Length;
                    info.Delete();
                    deleted++;
                    changedDirectories.Add(setDirectory);
                }
                catch (Exception ex)
                {
                    state.WriteLog($"无法删除: {file} | {ex.Message}");
                }
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
        if (changedDirectories.Count > 0)
        {
            foreach (var item in state.Database.LocalFiles.Where(item =>
                         item.StorageTier == StorageTiers.Local &&
                         changedDirectories.Contains(item.LocalDir)))
            {
                try
                {
                    item.TotalBytes = Directory.EnumerateFiles(
                            item.LocalDir,
                            "*",
                            SearchOption.AllDirectories)
                        .Sum(file => new FileInfo(file).Length);
                    item.LastScanned = DateTime.Now.ToString("s");
                }
                catch (Exception ex)
                {
                    state.WriteLog($"清理后统计更新失败: {item.LocalDir} | {ex.Message}");
                }
            }
            state.Database.Save();
            state.NotifyDataChanged();
        }
        return (deleted, bytes);
    }

    public static IReadOnlyList<EmptySetDirectory> FindEmptySetDirectories(AppState state)
    {
        var results = new List<EmptySetDirectory>();
        foreach (var entry in EnumeratePhysicalSetDirectories(state))
        {
            try
            {
                if (!Directory.EnumerateFiles(
                        entry.Path,
                        "*",
                        SearchOption.AllDirectories).Any())
                    results.Add(entry);
            }
            catch (Exception ex)
            {
                state.WriteLog($"空目录检查失败: {entry.Path} | {ex.Message}");
            }
        }
        return results;
    }

    public static EmptyDirectoryCleanupResult DeleteEmptySetDirectories(
        AppState state,
        IEnumerable<EmptySetDirectory> entries)
    {
        using var operationLease = ResourceOperationLock.TryAcquire() ??
                                   throw new InvalidOperationException(
                                       "下载、迁移或其他清理任务正在使用资源库，请稍后重试。");
        var requestedEntries = entries
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        var allowed = EnumeratePhysicalSetDirectories(state)
            .ToDictionary(
                item => Path.GetFullPath(item.Path).TrimEnd('\\'),
                item => item,
                StringComparer.OrdinalIgnoreCase);
        var deleted = 0;
        var failed = new List<string>();
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in requestedEntries)
        {
            var fullPath = Path.GetFullPath(entry.Path).TrimEnd('\\');
            if (!allowed.ContainsKey(fullPath))
            {
                failed.Add(entry.Path);
                state.WriteLog($"拒绝清理不在资源库套图层级的目录: {entry.Path}");
                continue;
            }
            try
            {
                if (!Directory.Exists(fullPath))
                    continue;
                if (Directory.EnumerateFiles(
                        fullPath,
                        "*",
                        SearchOption.AllDirectories).Any())
                {
                    failed.Add(entry.Path);
                    state.WriteLog($"空目录清理已跳过，目录中出现文件: {entry.Path}");
                    continue;
                }
                Directory.Delete(fullPath, recursive: true);
                deleted++;
                deletedPaths.Add(fullPath);
                DeleteParentIfEmpty(Path.GetDirectoryName(fullPath));
            }
            catch (Exception ex)
            {
                failed.Add(entry.Path);
                state.WriteLog($"空目录清理失败: {entry.Path} | {ex.Message}");
            }
        }
        if (deletedPaths.Count > 0)
        {
            state.Database.LocalFiles.RemoveAll(item =>
                deletedPaths.Contains(
                    Path.GetFullPath(item.LocalDir).TrimEnd('\\')));
            state.Database.Save();
        }
        foreach (var storageTier in requestedEntries
                     .Select(entry => entry.StorageTier)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            LocalScanner.RefreshZeroMediaEntries(
                state,
                storageTier,
                notify: false,
                confirmedDeletedPaths: deletedPaths);
        if (deleted > 0)
            state.NotifyDataChanged();
        return new EmptyDirectoryCleanupResult(deleted, failed);
    }

    public static async Task<(int Valid, int Invalid)> CheckVideosAsync(
        AppState state,
        CancellationToken token)
    {
        using var operationLease = ResourceOperationLock.TryAcquire() ??
                                   throw new InvalidOperationException(
                                       "下载、迁移或其他资源操作正在运行，请稍后重试。");
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
        LocalScanner.Scan(state, includeArchive: false);
        return (results.Count - invalidCount, invalidCount);
    }

    private static string[] ExistingSetDirectories(AppState state)
    {
        return state.Database.LocalFiles
            .Where(x => x.StorageTier == StorageTiers.Local)
            .Select(x => x.LocalDir)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<EmptySetDirectory> EnumeratePhysicalSetDirectories(AppState state)
    {
        var results = new Dictionary<string, EmptySetDirectory>(StringComparer.OrdinalIgnoreCase);
        AddModernRoot(state.Settings.DownloadRoot, StorageTiers.Local);
        if (!string.IsNullOrWhiteSpace(state.Settings.ArchiveRoot) &&
            Directory.Exists(state.Settings.ArchiveRoot))
            AddModernRoot(state.Settings.ArchiveRoot, StorageTiers.Archive);
        foreach (var legacyRoot in state.Settings.LegacyDownloadRoots
                     .Where(Directory.Exists)
                     .Where(root => !PathsEqual(root, state.Settings.DownloadRoot)))
            AddLegacyRoot(legacyRoot);
        return results.Values;

        void AddModernRoot(string root, string storageTier)
        {
            foreach (var category in LibraryPaths.Categories(state.Settings))
            {
                var categoryRoot = Path.Combine(root, category);
                if (!Directory.Exists(categoryRoot))
                    continue;
                foreach (var modelDirectory in Directory.EnumerateDirectories(categoryRoot)
                             .Where(IsUserDirectory))
                foreach (var setDirectory in Directory.EnumerateDirectories(modelDirectory)
                             .Where(IsUserDirectory))
                {
                    var fullPath = Path.GetFullPath(setDirectory).TrimEnd('\\');
                    results[fullPath] = new EmptySetDirectory(fullPath, storageTier);
                }
            }
        }

        void AddLegacyRoot(string root)
        {
            foreach (var modelDirectory in Directory.EnumerateDirectories(root)
                         .Where(IsUserDirectory))
            foreach (var setDirectory in Directory.EnumerateDirectories(modelDirectory)
                         .Where(IsUserDirectory))
            {
                var fullPath = Path.GetFullPath(setDirectory).TrimEnd('\\');
                results.TryAdd(
                    fullPath,
                    new EmptySetDirectory(fullPath, StorageTiers.Local));
            }
        }
    }

    private static bool IsUserDirectory(string path) =>
        !AppPaths.IsInsideTool(path) &&
        !Path.GetFileName(path).StartsWith('.') &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd('\\').Equals(
            Path.GetFullPath(right).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompleteDownload(string file) =>
        file.EndsWith(".BaiduPCS-Go-downloading", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
        file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
        File.Exists(file + ".BaiduPCS-Go-downloading") ||
        File.Exists(file + ".aria2");

    private static bool IsArchive(string file, Settings settings)
    {
        var name = Path.GetFileName(file);
        return LooksLikeArchive(file) ||
               settings.ArchiveExts.Contains(
                   Path.GetExtension(file),
                   StringComparer.OrdinalIgnoreCase) ||
               name.EndsWith(".7z.gz", StringComparison.OrdinalIgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(
                   name,
                   @"\.(?:7z|zip|rar)\.\d{3}$|\.part\d+\.rar$|\.r\d{2}$",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeArchive(string file)
    {
        try
        {
            Span<byte> header = stackalloc byte[512];
            using var stream = File.OpenRead(file);
            var read = stream.Read(header);
            if (read < 4)
                return false;
            return header[0] == 0x50 && header[1] == 0x4B &&
                   header[2] is 0x03 or 0x05 or 0x07 &&
                   header[3] is 0x04 or 0x06 or 0x08 ||
                   read >= 6 && header[..6].SequenceEqual(
                       new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }) ||
                   read >= 7 && header[..7].SequenceEqual(
                       new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }) ||
                   read >= 8 && header[..8].SequenceEqual(
                       new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }) ||
                   header[0] == 0x1F && header[1] == 0x8B ||
                   header[0] == (byte)'B' && header[1] == (byte)'Z' &&
                   header[2] == (byte)'h' ||
                   read >= 6 && header[..6].SequenceEqual(
                       new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }) ||
                   header[0] == 0x28 && header[1] == 0xB5 &&
                   header[2] == 0x2F && header[3] == 0xFD ||
                   header[0] == (byte)'M' && header[1] == (byte)'S' &&
                   header[2] == (byte)'C' && header[3] == (byte)'F' ||
                   read >= 262 && header[257] == (byte)'u' &&
                   header[258] == (byte)'s' && header[259] == (byte)'t' &&
                   header[260] == (byte)'a' && header[261] == (byte)'r';
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteParentIfEmpty(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
            // The set was removed successfully; a non-empty or busy model directory is harmless.
        }
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
