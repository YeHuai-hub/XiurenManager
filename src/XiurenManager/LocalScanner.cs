using XiurenDownloader;

namespace XiurenManager;

internal static class LocalScanner
{
    public static void ScanExclusive(
        AppState state,
        bool notify = true,
        bool includeArchive = true,
        CancellationToken token = default)
    {
        using var operationLease = ResourceOperationLock.TryAcquire() ??
                                   throw new InvalidOperationException(
                                       "下载、迁移或其他资源操作正在运行，请稍后重试。");
        Scan(state, notify, includeArchive, token);
    }

    public static void ScanModelsExclusive(
        AppState state,
        IEnumerable<ResourceItem> resources,
        bool notify = true,
        bool includeArchive = true,
        CancellationToken token = default)
    {
        using var operationLease = ResourceOperationLock.TryAcquire() ??
                                   throw new InvalidOperationException(
                                       "下载、迁移或其他资源操作正在运行，请稍后重试。");
        ScanModels(state, resources, notify, includeArchive, token);
    }

    public static void Scan(
        AppState state,
        bool notify = true,
        bool includeArchive = true,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = state.Settings.DownloadRoot;
        Directory.CreateDirectory(root);
        foreach (var category in LibraryPaths.Categories(state.Settings))
            Directory.CreateDirectory(Path.Combine(root, category));

        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, LocalStat>(StringComparer.OrdinalIgnoreCase);
        var previousArchive = state.Database.LocalFiles
            .Where(item => item.StorageTier.Equals(
                StorageTiers.Archive,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        ScanLibraryRoot(
            root,
            StorageTiers.Local,
            imageExts,
            videoExts,
            results,
            state,
            overwrite: true,
            token);

        var archiveRoot = state.Settings.ArchiveRoot;
        if (!includeArchive)
        {
            foreach (var item in state.Database.LocalFiles.Where(x =>
                         x.StorageTier.Equals(
                             StorageTiers.Archive,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var key = item.Category + "|" + item.Model + "|" + item.Title;
                results.TryAdd(key, item);
            }
        }
        else if (!string.IsNullOrWhiteSpace(archiveRoot))
        {
            if (Directory.Exists(archiveRoot))
            {
                var archiveResults = new Dictionary<string, LocalStat>(
                    StringComparer.OrdinalIgnoreCase);
                var failedArchiveDirectories = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                ScanLibraryRoot(
                    archiveRoot,
                    StorageTiers.Archive,
                    imageExts,
                    videoExts,
                    archiveResults,
                    state,
                    overwrite: true,
                    token,
                    failedArchiveDirectories);
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(archiveRoot))
                {
                    state.WriteLog("NAS 在资源库扫描过程中离线，本轮 NAS 结果未提交并保留原快照。");
                    foreach (var item in previousArchive)
                        results.TryAdd(StatKey(item), item);
                }
                else
                {
                    foreach (var item in previousArchive.Where(item =>
                                 failedArchiveDirectories.Contains(NormalizePath(item.LocalDir))))
                        archiveResults.TryAdd(StatKey(item), item);
                    foreach (var item in archiveResults.Values)
                        results.TryAdd(StatKey(item), item);
                }
            }
            else
            {
                state.WriteLog("NAS 资源库当前离线，保留上次扫描记录。");
                foreach (var item in previousArchive)
                    results.TryAdd(StatKey(item), item);
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
                storageTier: StorageTiers.Local,
                token);
        }

        token.ThrowIfCancellationRequested();
        state.Database.LocalFiles = results.Values
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReconcileResourceLocations(state, token);
        state.Database.Save();
        state.Metadata.QueueSync(state.Database.LocalFiles);
        var localCount = results.Values.Count(x => x.StorageTier == StorageTiers.Local);
        var archiveCount = results.Count - localCount;
        state.WriteLog($"资源库扫描完成: {results.Count} 套（本地 {localCount} / NAS {archiveCount}）");
        if (notify)
            state.NotifyDataChanged();
    }

    public static void ScanModels(
        AppState state,
        IEnumerable<ResourceItem> resources,
        bool notify = true,
        bool includeArchive = true,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var targets = resources
            .Select(x => (
                Category: LibraryPaths.NormalizeCategory(x.Category),
                Model: XiurenClient.Safe(x.Model)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Model))
            .GroupBy(x => ModelKey(x.Category, x.Model), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
        if (targets.Length == 0) return;

        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, LocalStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in state.Database.LocalFiles)
            results[StatKey(item)] = item;
        var targetKeys = targets
            .Select(x => ModelKey(x.Category, x.Model))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archiveOnline = includeArchive &&
                            !string.IsNullOrWhiteSpace(state.Settings.ArchiveRoot) &&
                            Directory.Exists(state.Settings.ArchiveRoot);
        var previousArchiveByTarget = state.Database.LocalFiles
            .Where(item => item.StorageTier.Equals(
                StorageTiers.Archive,
                StringComparison.OrdinalIgnoreCase))
            .Where(item => targetKeys.Contains(ModelKey(item.Category, item.Model)))
            .ToArray();

        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            var previous = results.Values.Where(x =>
                    ModelKey(x.Category, x.Model).Equals(
                        ModelKey(target.Category, target.Model),
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var key in results
                         .Where(x => previous.Contains(x.Value))
                         .Select(x => x.Key)
                         .ToArray())
                results.Remove(key);

            var localModel = LibraryPaths.ModelRoot(
                state.Settings,
                target.Category,
                target.Model);
            if (Directory.Exists(localModel))
                ScanModelDirectory(
                    localModel, target.Category, target.Model,
                    imageExts, videoExts, results, state,
                    overwrite: true, StorageTiers.Local, token);

            foreach (var legacyRoot in state.Settings.LegacyDownloadRoots
                         .Where(Directory.Exists)
                         .Where(x => target.Category.Equals(
                             LibraryPaths.DefaultCategory,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var legacyModel = Path.Combine(legacyRoot, target.Model);
                if (Directory.Exists(legacyModel))
                    ScanModelDirectory(
                        legacyModel, target.Category, target.Model,
                        imageExts, videoExts, results, state,
                        overwrite: false, StorageTiers.Local, token);
            }

            if (archiveOnline)
            {
                var archiveModel = LibraryPaths.ArchiveModelRoot(
                    state.Settings,
                    target.Category,
                    target.Model);
                if (!string.IsNullOrWhiteSpace(archiveModel) && Directory.Exists(archiveModel))
                {
                    var failedArchiveDirectories = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    ScanModelDirectory(
                        archiveModel, target.Category, target.Model,
                        imageExts, videoExts, results, state,
                        overwrite: false, StorageTiers.Archive, token,
                        failedArchiveDirectories);
                    foreach (var item in previous.Where(item =>
                                 item.StorageTier.Equals(
                                     StorageTiers.Archive,
                                     StringComparison.OrdinalIgnoreCase) &&
                                 failedArchiveDirectories.Contains(
                                     NormalizePath(item.LocalDir))))
                        results.TryAdd(StatKey(item), item);
                }
                else
                {
                    foreach (var item in previous.Where(item =>
                                 item.StorageTier.Equals(
                                     StorageTiers.Archive,
                                     StringComparison.OrdinalIgnoreCase)))
                        results.TryAdd(StatKey(item), item);
                }
            }
            else
            {
                foreach (var item in previous.Where(x =>
                             x.StorageTier.Equals(
                                 StorageTiers.Archive,
                                 StringComparison.OrdinalIgnoreCase)))
                    results.TryAdd(StatKey(item), item);
            }
        }

        token.ThrowIfCancellationRequested();
        if (archiveOnline && !Directory.Exists(state.Settings.ArchiveRoot))
        {
            foreach (var key in results
                         .Where(pair => pair.Value.StorageTier.Equals(
                             StorageTiers.Archive,
                             StringComparison.OrdinalIgnoreCase) &&
                             targetKeys.Contains(ModelKey(
                                 pair.Value.Category,
                                 pair.Value.Model)))
                         .Select(pair => pair.Key)
                         .ToArray())
                results.Remove(key);
            foreach (var item in previousArchiveByTarget)
                results.TryAdd(StatKey(item), item);
            state.WriteLog("NAS 在增量扫描过程中离线，本轮 NAS 结果未提交并保留原快照。");
        }
        state.Database.LocalFiles = results.Values
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReconcileResourceLocations(state, token, targetKeys);
        state.Database.Save();
        state.Metadata.QueueSync(state.Database.LocalFiles.Where(item =>
            targetKeys.Contains(ModelKey(item.Category, item.Model))));
        state.WriteLog($"资源库增量扫描完成: {targets.Length} 个模特。");
        if (notify)
            state.NotifyDataChanged();
    }

    public static void RefreshZeroMediaEntries(
        AppState state,
        string storageTier,
        bool notify = true,
        CancellationToken token = default,
        IReadOnlySet<string>? confirmedDeletedPaths = null)
    {
        token.ThrowIfCancellationRequested();
        if (storageTier.Equals(StorageTiers.Archive, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(state.Settings.ArchiveRoot) ||
             !Directory.Exists(state.Settings.ArchiveRoot)))
        {
            state.WriteLog("NAS 当前离线，零媒体记录复核已跳过并保留原统计快照。");
            return;
        }
        var targets = state.Database.LocalFiles
            .Where(item => item.StorageTier.Equals(
                             storageTier,
                             StringComparison.OrdinalIgnoreCase) &&
                         item.ImageCount + item.VideoCount + item.InvalidVideoCount == 0)
            .ToArray();
        if (targets.Length == 0)
            return;

        var targetSet = targets.ToHashSet();
        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = state.Database.LocalFiles
            .Where(item => !targetSet.Contains(item))
            .ToDictionary(StatKey, item => item, StringComparer.OrdinalIgnoreCase);
        var recovered = new List<LocalStat>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(target.LocalDir))
            {
                if (storageTier.Equals(
                        StorageTiers.Archive,
                        StringComparison.OrdinalIgnoreCase) &&
                    (confirmedDeletedPaths == null ||
                     !confirmedDeletedPaths.Contains(
                         Path.GetFullPath(target.LocalDir).TrimEnd('\\'))))
                    results.TryAdd(StatKey(target), target);
                continue;
            }
            try
            {
                var files = Directory.EnumerateFiles(
                        target.LocalDir,
                        "*",
                        SearchOption.AllDirectories)
                    .Where(path => !AppPaths.IsInsideTool(path))
                    .Select(path => new FileInfo(path))
                    .ToArray();
                var videos = files.Where(file => videoExts.Contains(file.Extension)).ToArray();
                var quickInvalid = videos.Count(file =>
                {
                    token.ThrowIfCancellationRequested();
                    return !VideoValidator.QuickHeaderLooksValid(file.FullName);
                });
                var invalidVideos = Math.Max(
                    quickInvalid,
                    VideoValidator.MarkedInvalidCount(target.LocalDir));
                var imageCount = files.Count(file =>
                {
                    token.ThrowIfCancellationRequested();
                    return imageExts.Contains(file.Extension) &&
                           MediaFileValidator.QuickImageHeaderLooksValid(file.FullName);
                });
                var videoCount = Math.Max(0, videos.Length - invalidVideos);
                if (imageCount + videoCount + invalidVideos == 0)
                    continue;

                var refreshed = new LocalStat
                {
                    Category = target.Category,
                    Model = target.Model,
                    Title = target.Title,
                    LocalDir = target.LocalDir,
                    StorageTier = target.StorageTier,
                    ImageCount = imageCount,
                    VideoCount = videoCount,
                    InvalidVideoCount = invalidVideos,
                    TotalBytes = files.Sum(file => file.Length),
                    LastScanned = DateTime.Now.ToString("s")
                };
                var key = StatKey(refreshed);
                if (storageTier == StorageTiers.Local || !results.ContainsKey(key))
                    results[key] = refreshed;
                recovered.Add(refreshed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.WriteLog($"零媒体记录复核失败: {target.LocalDir} | {ex.Message}");
                results.TryAdd(StatKey(target), target);
            }
        }

        if (storageTier.Equals(StorageTiers.Archive, StringComparison.OrdinalIgnoreCase) &&
            !Directory.Exists(state.Settings.ArchiveRoot))
        {
            state.WriteLog("NAS 在零媒体记录复核过程中离线，本轮结果未提交并保留原统计快照。");
            return;
        }

        state.Database.LocalFiles = results.Values
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.Database.Save();
        state.Metadata.QueueSync(recovered);
        state.WriteLog(
            $"零媒体记录复核完成: {storageTier}，检查 {targets.Length}，恢复 {recovered.Count}，" +
            $"移除空目录或待修复记录 {targets.Length - recovered.Count}");
        if (notify)
            state.NotifyDataChanged();
    }

    private static void ReconcileResourceLocations(
        AppState state,
        CancellationToken token,
        HashSet<string>? targetModels = null)
    {
        foreach (var resource in state.Database.Resources)
        {
            token.ThrowIfCancellationRequested();
            if (targetModels != null && !targetModels.Contains(
                    ModelKey(resource.Category, resource.Model)))
                continue;
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
        string storageTier = StorageTiers.Local,
        CancellationToken token = default,
        HashSet<string>? failedDirectories = null)
    {
        foreach (var modelDir in Directory.EnumerateDirectories(categoryRoot)
                     .Where(x => !AppPaths.IsInsideTool(x))
                     .Where(x => !Path.GetFileName(x).StartsWith('.')))
        {
            token.ThrowIfCancellationRequested();
            var model = Path.GetFileName(modelDir);
            ScanModelDirectory(
                modelDir,
                category,
                model,
                imageExts,
                videoExts,
                results,
                state,
                overwrite,
                storageTier,
                token,
                failedDirectories);
        }
    }

    private static void ScanModelDirectory(
        string modelDir,
        string category,
        string model,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        Dictionary<string, LocalStat> results,
        AppState state,
        bool overwrite,
        string storageTier,
        CancellationToken token,
        HashSet<string>? failedDirectories = null)
    {
        foreach (var setDir in Directory.EnumerateDirectories(modelDir)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var files = Directory.EnumerateFiles(setDir, "*", SearchOption.AllDirectories)
                    .Where(x => !AppPaths.IsInsideTool(x))
                    .Select(x =>
                    {
                        token.ThrowIfCancellationRequested();
                        return new FileInfo(x);
                    })
                    .ToArray();
                var videos = files.Where(x => videoExts.Contains(x.Extension)).ToArray();
                var quickInvalid = videos.Count(x =>
                {
                    token.ThrowIfCancellationRequested();
                    return !VideoValidator.QuickHeaderLooksValid(x.FullName);
                });
                var invalidVideos = Math.Max(
                    quickInvalid,
                    VideoValidator.MarkedInvalidCount(setDir));
                var imageCount = files.Count(x =>
                {
                    token.ThrowIfCancellationRequested();
                    return imageExts.Contains(x.Extension) &&
                           MediaFileValidator.QuickImageHeaderLooksValid(x.FullName);
                });
                var videoCount = Math.Max(0, videos.Length - invalidVideos);
                if (imageCount + videoCount + invalidVideos == 0)
                    continue;
                var item = new LocalStat
                {
                    Category = LibraryPaths.NormalizeCategory(category),
                    Model = model,
                    Title = Path.GetFileName(setDir),
                    LocalDir = setDir,
                    StorageTier = storageTier,
                    ImageCount = imageCount,
                    VideoCount = videoCount,
                    InvalidVideoCount = invalidVideos,
                    TotalBytes = files.Sum(x => x.Length),
                    LastScanned = DateTime.Now.ToString("s")
                };
                var key = StatKey(item);
                if (!overwrite && results.TryGetValue(key, out var existing) &&
                    !PathsEqual(existing.LocalDir, item.LocalDir))
                {
                    state.WriteLog(
                        $"检测到跨存储重复套图，优先保留本地记录: {item.Model} / {item.Title}");
                }
                else if (overwrite || !results.ContainsKey(key))
                    results[key] = item;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedDirectories?.Add(NormalizePath(setDir));
                state.WriteLog($"扫描失败: {setDir} | {ex.Message}");
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
        bool overwrite,
        CancellationToken token,
        HashSet<string>? failedDirectories = null)
    {
        foreach (var categoryDir in Directory.EnumerateDirectories(root)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            token.ThrowIfCancellationRequested();
            var category = Path.GetFileName(categoryDir);
            ScanCategory(
                categoryDir,
                category,
                imageExts,
                videoExts,
                results,
                state,
                overwrite,
                storageTier,
                token,
                failedDirectories);
        }
    }

    private static string StatKey(LocalStat item) =>
        ModelKey(item.Category, item.Model) + "|" + item.Title;

    private static string ModelKey(string category, string model) =>
        LibraryPaths.NormalizeCategory(category) + "|" + XiurenClient.Safe(model);

    private static bool PathsEqual(string left, string right)
    {
        return Path.GetFullPath(left).TrimEnd('\\')
            .Equals(
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value) =>
        Path.GetFullPath(value).TrimEnd('\\');

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
