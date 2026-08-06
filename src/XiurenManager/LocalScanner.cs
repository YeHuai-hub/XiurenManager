using XiurenDownloader;

namespace XiurenManager;

internal static class LocalScanner
{
    public static void Scan(AppState state, bool notify = true)
    {
        var root = state.Settings.DownloadRoot;
        Directory.CreateDirectory(root);
        foreach (var category in LibraryPaths.Categories(state.Settings))
            Directory.CreateDirectory(Path.Combine(root, category));

        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, LocalStat>(StringComparer.OrdinalIgnoreCase);

        ScanLibraryRoot(
            root,
            StorageTiers.Local,
            imageExts,
            videoExts,
            results,
            state,
            overwrite: true);

        var archiveRoot = state.Settings.ArchiveRoot;
        if (!string.IsNullOrWhiteSpace(archiveRoot))
        {
            if (Directory.Exists(archiveRoot))
            {
                ScanLibraryRoot(
                    archiveRoot,
                    StorageTiers.Archive,
                    imageExts,
                    videoExts,
                    results,
                    state,
                    overwrite: false);
            }
            else
            {
                state.WriteLog("NAS 资源库当前离线，保留上次扫描记录。");
                foreach (var item in state.Database.LocalFiles.Where(x =>
                             x.StorageTier.Equals(StorageTiers.Archive, StringComparison.OrdinalIgnoreCase)))
                {
                    var key = item.Category + "|" + item.Model + "|" + item.Title;
                    results.TryAdd(key, item);
                }
            }
        }

        foreach (var legacyRoot in state.Settings.LegacyDownloadRoots
                     .Where(Directory.Exists)
                     .Where(x => !PathsEqual(x, root)))
        {
            ScanCategory(
                legacyRoot,
                LibraryPaths.DefaultCategory,
                imageExts,
                videoExts,
                results,
                state,
                overwrite: false,
                storageTier: StorageTiers.Local);
        }

        state.Database.LocalFiles = results.Values
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReconcileResourceLocations(state);
        state.Database.Save();
        var localCount = results.Values.Count(x => x.StorageTier == StorageTiers.Local);
        var archiveCount = results.Count - localCount;
        state.WriteLog($"资源库扫描完成: {results.Count} 套（本地 {localCount} / NAS {archiveCount}）");
        if (notify)
            state.NotifyDataChanged();
    }

    private static void ReconcileResourceLocations(AppState state)
    {
        foreach (var resource in state.Database.Resources)
        {
            var modernPath = LibraryPaths.SetRoot(
                state.Settings,
                resource.Category,
                resource.Model,
                resource.Title);
            if (Directory.Exists(modernPath))
            {
                resource.LocalDir = modernPath;
                continue;
            }
            var archiveModelPath = LibraryPaths.ArchiveModelRoot(
                state.Settings,
                resource.Category,
                resource.Model);
            var archivePath = string.IsNullOrWhiteSpace(archiveModelPath)
                ? ""
                : Path.Combine(archiveModelPath, XiurenClient.Safe(resource.Title));
            if (!string.IsNullOrWhiteSpace(archivePath) && Directory.Exists(archivePath))
            {
                resource.LocalDir = archivePath;
                continue;
            }
            if (Directory.Exists(resource.LocalDir))
                continue;

            var migratedPath = state.Settings.LegacyDownloadRoots
                .Where(root => IsInside(resource.LocalDir, root))
                .Select(root => Path.Combine(
                    LibraryPaths.CategoryRoot(state.Settings, resource.Category),
                    Path.GetRelativePath(root, resource.LocalDir)))
                .FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrWhiteSpace(migratedPath))
            {
                resource.LocalDir = migratedPath;
                continue;
            }

            var match = state.Database.LocalFiles.FirstOrDefault(item =>
                item.Category.Equals(
                    resource.Category,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Model.Equals(
                    resource.Model,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Title.Equals(
                    resource.Title,
                    StringComparison.OrdinalIgnoreCase));
            if (match != null)
                resource.LocalDir = match.LocalDir;
        }
    }

    private static void ScanCategory(
        string categoryRoot,
        string category,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        Dictionary<string, LocalStat> results,
        AppState state,
        bool overwrite = true,
        string storageTier = StorageTiers.Local)
    {
        foreach (var modelDir in Directory.EnumerateDirectories(categoryRoot)
                     .Where(x => !AppPaths.IsInsideTool(x))
                     .Where(x => !Path.GetFileName(x).StartsWith('.')))
        {
            var model = Path.GetFileName(modelDir);
            foreach (var setDir in Directory.EnumerateDirectories(modelDir)
                         .Where(x => !AppPaths.IsInsideTool(x)))
            {
                try
                {
                    var files = Directory.EnumerateFiles(setDir, "*", SearchOption.AllDirectories)
                        .Where(x => !AppPaths.IsInsideTool(x))
                        .Select(x => new FileInfo(x))
                        .ToArray();
                    var videos = files.Where(x => videoExts.Contains(x.Extension)).ToArray();
                    var quickInvalid = videos.Count(x =>
                        !VideoValidator.QuickHeaderLooksValid(x.FullName));
                    var invalidVideos = Math.Max(
                        quickInvalid,
                        VideoValidator.MarkedInvalidCount(setDir));
                    var item = new LocalStat
                    {
                        Category = LibraryPaths.NormalizeCategory(category),
                        Model = model,
                        Title = Path.GetFileName(setDir),
                        LocalDir = setDir,
                        StorageTier = storageTier,
                        ImageCount = files.Count(x =>
                            imageExts.Contains(x.Extension) &&
                            MediaFileValidator.QuickImageHeaderLooksValid(x.FullName)),
                        VideoCount = Math.Max(0, videos.Length - invalidVideos),
                        InvalidVideoCount = invalidVideos,
                        TotalBytes = files.Sum(x => x.Length),
                        LastScanned = DateTime.Now.ToString("s")
                    };
                    var key = item.Category + "|" + item.Model + "|" + item.Title;
                    if (!overwrite && results.TryGetValue(key, out var existing) &&
                        !PathsEqual(existing.LocalDir, item.LocalDir))
                    {
                        state.WriteLog(
                            $"检测到跨存储重复套图，优先保留本地记录: {item.Model} / {item.Title}");
                    }
                    else if (overwrite || !results.ContainsKey(key))
                        results[key] = item;
                }
                catch (Exception ex)
                {
                    state.WriteLog($"扫描失败: {setDir} | {ex.Message}");
                }
            }
        }
    }

    private static void ScanLibraryRoot(
        string root,
        string storageTier,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        Dictionary<string, LocalStat> results,
        AppState state,
        bool overwrite)
    {
        foreach (var categoryDir in Directory.EnumerateDirectories(root)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            var category = Path.GetFileName(categoryDir);
            ScanCategory(
                categoryDir,
                category,
                imageExts,
                videoExts,
                results,
                state,
                overwrite,
                storageTier);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd('\\')
            .Equals(
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
