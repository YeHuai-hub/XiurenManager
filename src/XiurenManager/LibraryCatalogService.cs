using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using XiurenDownloader;

namespace XiurenManager;

internal enum ArchiveCatalogScanMode
{
    Skipped,
    Verified,
    Offline
}

internal sealed record CatalogMediaFile(string Path, bool IsVideo);

internal sealed class LibraryLedgerDocument
{
    public string Schema { get; set; } = "xiuren-library-ledger/v1";
    public string UpdatedAt { get; set; } = DateTime.Now.ToString("s");
    public List<LocalStat> Items { get; set; } = [];
}

internal sealed class SetManifestDocument
{
    public string Schema { get; set; } = "xiuren-set-manifest/v1";
    public string SetId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Model { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceDirectory { get; set; } = "";
    public string StorageTier { get; set; } = "";
    public string SourcePostId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string PanUrl { get; set; } = "";
    public string PanPassword { get; set; } = "";
    public string ExtractPassword { get; set; } = "";
    public string VerifiedAt { get; set; } = "";
    public List<SetManifestFile> Files { get; set; } = [];
    public List<MergedPartInfo> MergedParts { get; set; } = [];
}

internal sealed class SetManifestFile
{
    public string RelativePath { get; set; } = "";
    public string MediaType { get; set; } = "";
    public long Bytes { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public bool IsUsable { get; set; } = true;
}

internal sealed record CoverRequest(LocalStat Item, string SourcePath);

internal sealed record SourceMetadataIndex(
    IReadOnlyDictionary<string, ResourceItem> ByPath,
    IReadOnlyDictionary<string, ResourceItem> ByTitle);

internal sealed class LibraryCatalogService : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly ConcurrentDictionary<string, object> ManifestGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AppState state;
    private readonly object gate = new();
    private readonly Channel<CoverRequest> coverQueue = Channel.CreateUnbounded<CoverRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource stopping = new();
    private readonly Task coverWorker;
    private Task? coverBackfillWorker;
    private int urgentCoverRequests;
    private List<LocalStat> entries;
    private SourceMetadataIndex sourceMetadataIndex = BuildSourceMetadataIndex([]);
    private string lastLedgerFingerprint = "";

    public LibraryCatalogService(AppState state)
    {
        this.state = state;
        entries = LoadOrCreate(state.Database.LocalFiles);
        sourceMetadataIndex = BuildSourceMetadataIndex(state.Database.ResourceSnapshot());
        lock (gate)
        {
            EnsureEntriesLocked();
            MergeDatabaseAdditionsLocked(state.Database.LocalFiles);
            var databaseNeedsSave = !CatalogFingerprint(state.Database.LocalFiles)
                .Equals(CatalogFingerprint(entries), StringComparison.Ordinal);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: databaseNeedsSave);
        }
        state.Favorites.ReconcileSetIds(Snapshot());
        coverWorker = Task.Run(ProcessCoverQueueAsync);
    }

    public LocalStat[] Snapshot(bool includeDeleted = true)
    {
        lock (gate)
            return entries
                .Where(item => includeDeleted || !IsStatus(item, CatalogStatuses.Deleted))
                .Select(Clone)
                .ToArray();
    }

    public string EnsureSetId(LocalStat item)
    {
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(item.SetId))
                return item.SetId;
            var existing = FindEntryLocked(item);
            item.SetId = existing?.SetId ?? NewSetId();
            return item.SetId;
        }
    }

    public void RefreshSourceMetadataIndex()
    {
        var refreshed = BuildSourceMetadataIndex(state.Database.ResourceSnapshot());
        lock (gate)
        {
            sourceMetadataIndex = refreshed;
            foreach (var item in entries)
                EnrichSourceMetadata(item, sourceMetadataIndex);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
    }

    public IReadOnlyList<LocalStat> MergeObserved(
        IEnumerable<LocalStat> observedItems,
        bool verifyLocal,
        ArchiveCatalogScanMode archiveMode,
        IReadOnlySet<string>? modelScope = null)
    {
        var now = DateTime.Now.ToString("s");
        lock (gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var observed in observedItems.Where(item => InScope(item, modelScope)))
            {
                var isArchive = IsArchive(observed);
                if ((!isArchive && !verifyLocal) ||
                    (isArchive && archiveMode != ArchiveCatalogScanMode.Verified))
                {
                    continue;
                }

                var existing = FindEntryLocked(observed);
                if (existing == null)
                {
                    existing = Clone(observed);
                    existing.SetId = string.IsNullOrWhiteSpace(observed.SetId)
                        ? NewSetId()
                        : observed.SetId;
                    entries.Add(existing);
                }
                CopyObserved(existing, observed, now);
                observed.SetId = existing.SetId;
                seen.Add(existing.SetId);
            }

            foreach (var item in entries.Where(item => InScope(item, modelScope)))
            {
                if (IsStatus(item, CatalogStatuses.Deleted))
                    continue;
                if (IsArchive(item))
                {
                    if (archiveMode == ArchiveCatalogScanMode.Offline)
                    {
                        SetUnavailable(item, CatalogStatuses.Offline, "NAS 当前离线", now);
                    }
                    else if (archiveMode == ArchiveCatalogScanMode.Verified &&
                             !seen.Contains(item.SetId))
                    {
                        SetUnavailable(item, CatalogStatuses.Missing, "最近扫描未找到该套目录", now);
                    }
                }
                else if (verifyLocal && !seen.Contains(item.SetId))
                {
                    SetUnavailable(item, CatalogStatuses.Missing, "最近扫描未找到该套目录", now);
                }
            }

            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
            return state.Database.LocalFiles.Select(Clone).ToArray();
        }
    }

    public void RecordManifest(LocalStat item, IReadOnlyCollection<FileInfo> files)
    {
        EnsureSetId(item);
        SourceMetadataIndex index;
        lock (gate)
        {
            index = sourceMetadataIndex;
            var existing = FindEntryLocked(item);
            if (existing != null && existing.MergedParts.Count > 0)
                item.MergedParts = CloneMergedParts(existing.MergedParts);
        }
        EnrichSourceMetadata(item, index);
        SetManifestDocument manifest;
        lock (ManifestGate(item.SetId))
        {
            manifest = BuildManifest(item, files, state.Settings);
            WriteManifestUnlocked(manifest);
        }
        var coverSource = manifest.Files
            .Where(file => file.IsUsable && file.MediaType == "Image")
            .Concat(manifest.Files.Where(file => file.IsUsable && file.MediaType == "Video"))
            .Select(file => Path.Combine(item.LocalDir, file.RelativePath))
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(coverSource) &&
            !File.Exists(CoverPath(item.SetId)))
        {
            coverQueue.Writer.TryWrite(new CoverRequest(Clone(item), coverSource));
        }
    }

    public async Task<IReadOnlyList<CatalogMediaFile>> LoadMediaAsync(
        LocalStat item,
        CancellationToken token)
    {
        if (IsStatus(item, CatalogStatuses.Deleted))
            throw new InvalidOperationException("该套资源已由用户删除，账本记录仅用于历史和恢复。");
        EnsureSetId(item);
        var directoryExists = await Task.Run(
            () => Directory.Exists(item.LocalDir),
            token).ConfigureAwait(false);
        if (!directoryExists)
        {
            var archiveOffline = IsArchive(item) &&
                                 !string.IsNullOrWhiteSpace(state.Settings.ArchiveRoot) &&
                                 !await Task.Run(
                                     () => Directory.Exists(state.Settings.ArchiveRoot),
                                     token).ConfigureAwait(false);
            MarkUnavailable(
                item,
                archiveOffline ? CatalogStatuses.Offline : CatalogStatuses.Missing,
                archiveOffline ? "NAS 当前离线" : "原始目录不存在");
            throw new DirectoryNotFoundException(
                archiveOffline ? "NAS 当前离线，已保留套图账本。" : "原始目录不存在，已在账本中标记缺失。");
        }

        var manifestResult = await Task.Run(() =>
        {
            lock (ManifestGate(item.SetId))
            {
                var current = TryReadManifestUnlocked(item.SetId);
                var needsRefresh = current == null || current.Files.Any(file =>
                    !File.Exists(Path.Combine(item.LocalDir, file.RelativePath)));
                if (!needsRefresh)
                    return (Manifest: current, Refreshed: false);
                current = BuildManifestFromDirectory(item, state.Settings, token);
                WriteManifestUnlocked(current);
                return (Manifest: current, Refreshed: true);
            }
        }, token).ConfigureAwait(false);
        var manifest = manifestResult.Manifest;
        if (manifestResult.Refreshed)
        {
            UpdateFromManifest(item, manifest);
            var coverSource = manifest.Files
                .Where(file => file.IsUsable && file.MediaType == "Image")
                .Concat(manifest.Files.Where(file => file.IsUsable && file.MediaType == "Video"))
                .Select(file => Path.Combine(item.LocalDir, file.RelativePath))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(coverSource))
                coverQueue.Writer.TryWrite(new CoverRequest(Clone(item), coverSource));
        }

        return manifest!.Files
            .Where(file => file.IsUsable)
            .Where(file => file.MediaType is "Image" or "Video")
            .OrderBy(file => NaturalSortKey(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(file => new CatalogMediaFile(
                Path.Combine(item.LocalDir, file.RelativePath),
                file.MediaType == "Video"))
            .ToArray();
    }

    public Task<System.Windows.Media.ImageSource?> LoadCoverAsync(
        LocalStat item,
        CancellationToken token,
        int decodeWidth = 440)
    {
        EnsureSetId(item);
        return MediaCoverService.LoadPersistentCoverAsync(
            CoverPath(item.SetId),
            token,
            decodeWidth);
    }

    public async Task<System.Windows.Media.ImageSource?> EnsureCoverAsync(
        LocalStat item,
        IReadOnlyList<CatalogMediaFile> media,
        CancellationToken token,
        int decodeWidth = 1200)
    {
        var cached = await LoadCoverAsync(item, token, decodeWidth).ConfigureAwait(false);
        if (cached != null) return cached;
        var source = media.FirstOrDefault()?.Path;
        if (string.IsNullOrWhiteSpace(source)) return null;

        Interlocked.Increment(ref urgentCoverRequests);
        try
        {
            using var operationLease = await ResourceOperationLock.AcquireAsync(token)
                .ConfigureAwait(false);
            await MediaCoverService.CreatePersistentCoverAsync(
                source,
                CoverPath(item.SetId),
                state.Settings,
                token,
                decodeWidth).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref urgentCoverRequests);
        }
        return await LoadCoverAsync(item, token, decodeWidth).ConfigureAwait(false);
    }

    public void MarkUnavailable(LocalStat item, string status, string reason)
    {
        lock (gate)
        {
            var existing = FindEntryLocked(item);
            if (existing == null) return;
            if (IsStatus(existing, CatalogStatuses.Deleted))
            {
                CopyCatalogFields(item, existing);
                return;
            }
            SetUnavailable(existing, status, reason, DateTime.Now.ToString("s"));
            CopyCatalogFields(item, existing);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
        state.NotifyDataChanged();
    }

    public void MarkDeletedPaths(IEnumerable<string> paths, string reason)
    {
        var normalized = paths
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0) return;
        var now = DateTime.Now.ToString("s");
        lock (gate)
        {
            foreach (var item in entries.Where(item =>
                         normalized.Contains(NormalizePath(item.LocalDir))))
                SetUnavailable(item, CatalogStatuses.Deleted, reason, now);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
        state.NotifyDataChanged();
    }

    public void UpdateLocation(
        LocalStat item,
        string target,
        string category,
        string model,
        string title)
    {
        lock (gate)
        {
            var existing = FindEntryLocked(item);
            if (existing == null) return;
            existing.LocalDir = target;
            existing.Category = LibraryPaths.NormalizeCategory(category);
            existing.Model = model;
            existing.Title = title;
            existing.StorageTier = DetectStorageTier(target);
            existing.Availability = CatalogStatuses.Available;
            existing.AvailabilityReason = "";
            existing.MissingSince = "";
            existing.LastVerified = DateTime.Now.ToString("s");
            UpdateManifestLocation(existing);
            CopyCatalogFields(item, existing);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
    }

    public void ReconcileLocations(IEnumerable<LocalStat> updatedItems)
    {
        lock (gate)
        {
            foreach (var updated in updatedItems)
            {
                var existing = FindEntryLocked(updated);
                if (existing == null) continue;
                var locationChanged = !existing.Category.Equals(
                                          LibraryPaths.NormalizeCategory(updated.Category),
                                          StringComparison.OrdinalIgnoreCase) ||
                                      !existing.Model.Equals(updated.Model, StringComparison.OrdinalIgnoreCase) ||
                                      !existing.Title.Equals(updated.Title, StringComparison.OrdinalIgnoreCase) ||
                                      !NormalizePath(existing.LocalDir).Equals(
                                          NormalizePath(updated.LocalDir),
                                          StringComparison.OrdinalIgnoreCase) ||
                                      !existing.StorageTier.Equals(
                                          updated.StorageTier,
                                          StringComparison.OrdinalIgnoreCase);
                existing.Category = LibraryPaths.NormalizeCategory(updated.Category);
                existing.Model = updated.Model;
                existing.Title = updated.Title;
                existing.LocalDir = updated.LocalDir;
                existing.StorageTier = updated.StorageTier;
                if (locationChanged)
                    UpdateManifestLocation(existing);
                CopyCatalogFields(updated, existing);
            }
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
    }

    public void AddCopy(LocalStat source, LocalStat copy)
    {
        CopySourceFields(copy, source);
        EnsureExpectedCounts(copy);
        copy.SetId = NewSetId();
        copy.Availability = CatalogStatuses.Available;
        copy.AvailabilityReason = "";
        copy.MissingSince = "";
        copy.LastVerified = DateTime.Now.ToString("s");
        copy.LastComplete = copy.InvalidVideoCount == 0 ? copy.LastVerified : "";
        lock (gate)
        {
            entries.Add(Clone(copy));
            var sourceManifest = TryReadManifest(source.SetId);
            if (sourceManifest != null)
            {
                sourceManifest.SetId = copy.SetId;
                sourceManifest.Category = copy.Category;
                sourceManifest.Model = copy.Model;
                sourceManifest.Title = copy.Title;
                sourceManifest.SourceDirectory = copy.LocalDir;
                sourceManifest.StorageTier = copy.StorageTier;
                WriteManifest(sourceManifest);
            }
            var sourceCover = CoverPath(source.SetId);
            var targetCover = CoverPath(copy.SetId);
            if (File.Exists(sourceCover))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetCover)!);
                File.Copy(sourceCover, targetCover, true);
            }
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
    }

    public void MergeSets(
        IReadOnlyList<LocalStat> sources,
        LocalStat merged,
        IReadOnlyList<string> partDirectories,
        string reason)
    {
        if (sources.Count != partDirectories.Count)
            throw new ArgumentException("合并来源与分卷目录数量不一致。");
        EnsureExpectedCounts(merged);
        merged.SetId = NewSetId();
        if (string.IsNullOrWhiteSpace(merged.Availability))
            merged.Availability = CatalogStatuses.Available;
        merged.MissingSince = "";
        merged.LastVerified = DateTime.Now.ToString("s");
        if (merged.Availability.Equals(CatalogStatuses.Available, StringComparison.OrdinalIgnoreCase))
            merged.LastComplete = merged.LastVerified;

        lock (gate)
        {
            for (var index = 0; index < sources.Count; index++)
            {
                var existing = FindEntryLocked(sources[index]);
                if (existing == null) continue;
                existing.LocalDir = partDirectories[index];
                existing.StorageTier = DetectStorageTier(existing.LocalDir);
                SetUnavailable(existing, CatalogStatuses.Deleted, reason, merged.LastVerified);
                CopyCatalogFields(sources[index], existing);
            }
            entries.Add(Clone(merged));
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
        foreach (var source in sources)
        {
            try { UpdateManifestLocation(source); }
            catch (Exception ex) { state.WriteLog($"合并后分卷清单更新失败: {source.Title} | {ex.Message}"); }
        }
        try
        {
            var firstCover = sources.Select(source => CoverPath(source.SetId))
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(firstCover))
            {
                var targetCover = CoverPath(merged.SetId);
                Directory.CreateDirectory(Path.GetDirectoryName(targetCover)!);
                File.Copy(firstCover, targetCover, true);
            }
        }
        catch (Exception ex)
        {
            state.WriteLog("合并套图封面继承失败，将在打开媒体库时重新生成: " + ex.Message);
        }
    }

    public void MarkMediaDeleted(LocalStat item, string deletedPath)
    {
        SetManifestDocument? manifest;
        lock (ManifestGate(item.SetId))
        {
            manifest = TryReadManifestUnlocked(item.SetId);
            if (manifest == null) return;
            var relative = Path.GetRelativePath(item.LocalDir, deletedPath);
            manifest.Files.RemoveAll(file => file.RelativePath.Equals(
                relative,
                StringComparison.OrdinalIgnoreCase));
            manifest.VerifiedAt = DateTime.Now.ToString("s");
            WriteManifestUnlocked(manifest);
        }
        if (manifest != null)
        {
            UpdateFromManifest(item, manifest);
        }
    }

    public void StartBackgroundCoverBackfill()
    {
        lock (gate)
        {
            if (coverBackfillWorker is { IsCompleted: false }) return;
            coverBackfillWorker = Task.Run(BackfillCoversAsync);
        }
    }

    public void Dispose()
    {
        coverQueue.Writer.TryComplete();
        stopping.Cancel();
        try { coverBackfillWorker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { coverWorker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        stopping.Dispose();
    }

    public static string CoverPath(string setId) =>
        string.IsNullOrWhiteSpace(setId)
            ? ""
            : Path.Combine(AppPaths.CoverCacheDir, setId + ".jpg");

    public static IReadOnlyList<CatalogMediaFile> ReadCachedMedia(LocalStat item)
    {
        var manifest = TryReadManifest(item.SetId);
        if (manifest == null) return [];
        return manifest.Files
            .Where(file => file.IsUsable)
            .Where(file => file.MediaType is "Image" or "Video")
            .OrderBy(file => NaturalSortKey(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .Select(file => new CatalogMediaFile(
                Path.Combine(item.LocalDir, file.RelativePath),
                file.MediaType == "Video"))
            .ToArray();
    }

    private async Task ProcessCoverQueueAsync()
    {
        try
        {
            await foreach (var request in coverQueue.Reader.ReadAllAsync(stopping.Token))
            {
                var output = CoverPath(request.Item.SetId);
                if (string.IsNullOrWhiteSpace(output) || File.Exists(output))
                    continue;
                try
                {
                    IDisposable? operationLease = null;
                    while (operationLease == null)
                    {
                        while (state.Queue.IsRunning || state.Storage.IsRunning)
                            await Task.Delay(500, stopping.Token).ConfigureAwait(false);
                        while (Volatile.Read(ref urgentCoverRequests) > 0)
                            await Task.Delay(250, stopping.Token).ConfigureAwait(false);
                        operationLease = ResourceOperationLock.TryAcquire();
                        if (operationLease == null)
                            await Task.Delay(250, stopping.Token).ConfigureAwait(false);
                    }
                    using (operationLease)
                    {
                        await MediaCoverService.CreatePersistentCoverAsync(
                            request.SourcePath,
                            output,
                            state.Settings,
                            stopping.Token).ConfigureAwait(false);
                    }
                    await Task.Delay(100, stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    state.WriteLog($"封面缓存生成失败: {request.Item.Title} | {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task BackfillCoversAsync()
    {
        var candidates = Snapshot(includeDeleted: false)
            .Where(item => !IsStatus(item, CatalogStatuses.Missing) &&
                           !IsStatus(item, CatalogStatuses.Offline))
            .Where(item => !File.Exists(CoverPath(item.SetId)))
            .OrderBy(item => IsArchive(item))
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return;

        var completed = 0;
        var failed = 0;
        state.WriteLog($"后台封面索引开始: {candidates.Length} 套待处理，不阻塞媒体库。");
        foreach (var item in candidates)
        {
            stopping.Token.ThrowIfCancellationRequested();
            while (state.Queue.IsRunning || state.Storage.IsRunning)
                await Task.Delay(1000, stopping.Token).ConfigureAwait(false);
            while (Volatile.Read(ref urgentCoverRequests) > 0)
                await Task.Delay(250, stopping.Token).ConfigureAwait(false);

            IDisposable? operationLease = null;
            try
            {
                while (operationLease == null)
                {
                    operationLease = ResourceOperationLock.TryAcquire();
                    if (operationLease == null)
                        await Task.Delay(1000, stopping.Token).ConfigureAwait(false);
                }
                var source = await Task.Run(
                    () => FindFirstCoverSource(item, state.Settings, stopping.Token),
                    stopping.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(source))
                {
                    await MediaCoverService.CreatePersistentCoverAsync(
                        source,
                        CoverPath(item.SetId),
                        state.Settings,
                        stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                if (failed <= 10 || failed % 100 == 0)
                    state.WriteLog($"后台封面跳过: {item.Title} | {ex.Message}");
            }
            finally
            {
                operationLease?.Dispose();
            }

            completed++;
            if (completed % 100 == 0)
                state.WriteLog($"后台封面索引进度: {completed}/{candidates.Length}");
            await Task.Delay(80, stopping.Token).ConfigureAwait(false);
        }
        state.WriteLog($"后台封面索引完成: {completed - failed}/{candidates.Length}，跳过 {failed}");
    }

    private void UpdateFromManifest(LocalStat item, SetManifestDocument manifest)
    {
        var images = manifest.Files.Count(file => file.MediaType == "Image" && file.IsUsable);
        var videos = manifest.Files.Count(file => file.MediaType == "Video" && file.IsUsable);
        var invalid = manifest.Files.Count(file => file.MediaType == "Video" && !file.IsUsable);
        lock (gate)
        {
            var existing = FindEntryLocked(item);
            if (existing == null) return;
            EnsureExpectedCounts(existing);
            existing.ImageCount = images;
            existing.VideoCount = videos;
            existing.InvalidVideoCount = invalid;
            existing.TotalBytes = manifest.Files.Sum(file => file.Bytes);
            existing.LastScanned = manifest.VerifiedAt;
            existing.LastVerified = manifest.VerifiedAt;
            var partial = images < existing.ExpectedImageCount ||
                          videos + invalid < existing.ExpectedVideoCount;
            existing.Availability = invalid > 0
                ? CatalogStatuses.Corrupt
                : partial
                    ? CatalogStatuses.Partial
                    : CatalogStatuses.Available;
            existing.AvailabilityReason = invalid > 0
                ? $"检测到 {invalid} 个损坏视频"
                : partial
                    ? $"当前 {images} 图 {videos} 视，历史完整记录为 " +
                      $"{existing.ExpectedImageCount} 图 {existing.ExpectedVideoCount} 视"
                    : "";
            existing.MissingSince = "";
            if (invalid == 0 && !partial)
            {
                existing.ExpectedImageCount = Math.Max(existing.ExpectedImageCount, images);
                existing.ExpectedVideoCount = Math.Max(existing.ExpectedVideoCount, videos);
                existing.ExpectedTotalBytes = Math.Max(existing.ExpectedTotalBytes, existing.TotalBytes);
                existing.LastComplete = manifest.VerifiedAt;
            }
            CopyCatalogFields(item, existing);
            SaveLedgerLocked();
            SyncDatabaseLocked(save: true);
        }
    }

    private List<LocalStat> LoadOrCreate(IEnumerable<LocalStat> databaseItems)
    {
        AppPaths.Ensure();
        if (File.Exists(AppPaths.LibraryLedgerFile))
        {
            try
            {
                var document = JsonSerializer.Deserialize<LibraryLedgerDocument>(
                    File.ReadAllText(AppPaths.LibraryLedgerFile, Encoding.UTF8),
                    Settings.JsonOptions);
                if (document?.Items != null)
                {
                    lastLedgerFingerprint = CatalogFingerprint(document.Items);
                    return document.Items;
                }
            }
            catch (Exception ex)
            {
                state.WriteLog("资源账本读取失败，将从主数据库恢复: " + ex.Message);
            }
        }

        CreateMigrationBackup();
        return databaseItems.Select(Clone).ToList();
    }

    private void CreateMigrationBackup()
    {
        try
        {
            var root = Path.Combine(
                AppPaths.DataDir,
                "backups",
                "catalog-migration-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            if (File.Exists(AppPaths.DbFile))
                File.Copy(AppPaths.DbFile, Path.Combine(root, "xiuren.db"), true);
            if (File.Exists(AppPaths.FavoritesFile))
                File.Copy(AppPaths.FavoritesFile, Path.Combine(root, "favorites.json"), true);
            if (File.Exists(AppPaths.LibraryLedgerFile))
                File.Copy(
                    AppPaths.LibraryLedgerFile,
                    Path.Combine(root, "library-ledger-v1.corrupt.json"),
                    true);
        }
        catch (Exception ex)
        {
            state.WriteLog("资源账本迁移备份失败: " + ex.Message);
        }
    }

    private void MergeDatabaseAdditionsLocked(IEnumerable<LocalStat> databaseItems)
    {
        foreach (var item in databaseItems)
        {
            var existing = FindEntryLocked(item);
            if (existing != null)
            {
                item.SetId = existing.SetId;
                continue;
            }
            var added = Clone(item);
            added.SetId = string.IsNullOrWhiteSpace(added.SetId) ? NewSetId() : added.SetId;
            NormalizeStatus(added);
            entries.Add(added);
        }
    }

    private void EnsureEntriesLocked()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (string.IsNullOrWhiteSpace(item.SetId) || !used.Add(item.SetId))
                item.SetId = NewSetId();
            used.Add(item.SetId);
            item.Category = LibraryPaths.NormalizeCategory(item.Category);
            NormalizeStatus(item);
            EnrichSourceMetadata(item, sourceMetadataIndex);
        }
    }

    private void SaveLedgerLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LibraryLedgerFile)!);
        var sortedItems = entries
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();
        var fingerprint = CatalogFingerprint(sortedItems);
        if (fingerprint.Equals(lastLedgerFingerprint, StringComparison.Ordinal) &&
            File.Exists(AppPaths.LibraryLedgerFile))
            return;
        var document = new LibraryLedgerDocument
        {
            UpdatedAt = DateTime.Now.ToString("s"),
            Items = sortedItems
        };
        AtomicWrite(
            AppPaths.LibraryLedgerFile,
            JsonSerializer.Serialize(document, Settings.JsonOptions));
        lastLedgerFingerprint = fingerprint;
    }

    private void SyncDatabaseLocked(bool save)
    {
        state.Database.LocalFiles = entries.Select(Clone).ToList();
        if (save) state.Database.Save();
    }

    private LocalStat? FindEntryLocked(LocalStat item)
    {
        if (!string.IsNullOrWhiteSpace(item.SetId))
        {
            var byId = entries.FirstOrDefault(entry => entry.SetId.Equals(
                item.SetId,
                StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        var path = NormalizePath(item.LocalDir);
        if (!string.IsNullOrWhiteSpace(path))
        {
            var byPath = entries.FirstOrDefault(entry => NormalizePath(entry.LocalDir).Equals(
                path,
                StringComparison.OrdinalIgnoreCase));
            if (byPath != null) return byPath;
        }
        return entries.FirstOrDefault(entry =>
            entry.Category.Equals(item.Category, StringComparison.OrdinalIgnoreCase) &&
            entry.Model.Equals(item.Model, StringComparison.OrdinalIgnoreCase) &&
            entry.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) &&
            entry.StorageTier.Equals(item.StorageTier, StringComparison.OrdinalIgnoreCase));
    }

    private static SetManifestDocument BuildManifestFromDirectory(
        LocalStat item,
        Settings settings,
        CancellationToken token)
    {
        var files = Directory.EnumerateFiles(
                item.LocalDir,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !AppPaths.IsInsideTool(path))
            .Select(path =>
            {
                token.ThrowIfCancellationRequested();
                return new FileInfo(path);
            })
            .ToArray();
        return BuildManifest(item, files, settings, token);
    }

    private static SetManifestDocument BuildManifest(
        LocalStat item,
        IEnumerable<FileInfo> files,
        Settings settings,
        CancellationToken token = default)
    {
        var images = settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videos = settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifest = new SetManifestDocument
        {
            SetId = item.SetId,
            Category = item.Category,
            Model = item.Model,
            Title = item.Title,
            SourceDirectory = item.LocalDir,
            StorageTier = item.StorageTier,
            SourcePostId = item.SourcePostId,
            SourceUrl = item.SourceUrl,
            PanUrl = item.PanUrl,
            PanPassword = item.PanPassword,
            ExtractPassword = item.ExtractPassword,
            VerifiedAt = DateTime.Now.ToString("s"),
            MergedParts = CloneMergedParts(item.MergedParts)
        };
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var isImage = images.Contains(file.Extension);
            var isVideo = videos.Contains(file.Extension);
            if (!isImage && !isVideo) continue;
            manifest.Files.Add(new SetManifestFile
            {
                RelativePath = Path.GetRelativePath(item.LocalDir, file.FullName),
                MediaType = isVideo ? "Video" : "Image",
                Bytes = file.Length,
                LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                IsUsable = isVideo
                    ? VideoValidator.QuickHeaderLooksValid(file.FullName)
                    : MediaFileValidator.QuickImageHeaderLooksValid(file.FullName)
            });
        }
        return manifest;
    }

    private static SetManifestDocument? TryReadManifest(string setId)
    {
        if (string.IsNullOrWhiteSpace(setId)) return null;
        lock (ManifestGate(setId))
            return TryReadManifestUnlocked(setId);
    }

    private static SetManifestDocument? TryReadManifestUnlocked(string setId)
    {
        var path = ManifestPath(setId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SetManifestDocument>(
                File.ReadAllText(path, Encoding.UTF8),
                Settings.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteManifest(SetManifestDocument manifest)
    {
        lock (ManifestGate(manifest.SetId))
            WriteManifestUnlocked(manifest);
    }

    private static void WriteManifestUnlocked(SetManifestDocument manifest)
    {
        var path = ManifestPath(manifest.SetId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, JsonSerializer.Serialize(manifest, Settings.JsonOptions));
    }

    private static object ManifestGate(string setId) =>
        ManifestGates.GetOrAdd(setId, static _ => new object());

    private static void UpdateManifestLocation(LocalStat item)
    {
        lock (ManifestGate(item.SetId))
        {
            var manifest = TryReadManifestUnlocked(item.SetId);
            if (manifest == null) return;
            manifest.Category = item.Category;
            manifest.Model = item.Model;
            manifest.Title = item.Title;
            manifest.SourceDirectory = item.LocalDir;
            manifest.StorageTier = item.StorageTier;
            manifest.SourcePostId = item.SourcePostId;
            manifest.SourceUrl = item.SourceUrl;
            manifest.PanUrl = item.PanUrl;
            manifest.PanPassword = item.PanPassword;
            manifest.ExtractPassword = item.ExtractPassword;
            WriteManifestUnlocked(manifest);
        }
    }

    private static string ManifestPath(string setId)
    {
        var safe = string.IsNullOrWhiteSpace(setId) ? NewSetId() : setId;
        var prefix = safe.Length >= 2 ? safe[..2] : "00";
        return Path.Combine(AppPaths.ManifestDir, prefix, safe + ".json");
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static void CopyObserved(LocalStat target, LocalStat source, string now)
    {
        EnsureExpectedCounts(target);
        var expectedImages = Math.Max(target.ExpectedImageCount, source.ExpectedImageCount);
        expectedImages = Math.Max(expectedImages, source.ImageCount);
        var sourceVideos = source.VideoCount + source.InvalidVideoCount;
        var expectedVideos = Math.Max(target.ExpectedVideoCount, source.ExpectedVideoCount);
        expectedVideos = Math.Max(expectedVideos, sourceVideos);
        var expectedBytes = Math.Max(target.ExpectedTotalBytes, source.ExpectedTotalBytes);
        expectedBytes = Math.Max(expectedBytes, source.TotalBytes);
        target.Category = LibraryPaths.NormalizeCategory(source.Category);
        target.Model = source.Model;
        target.Title = source.Title;
        target.LocalDir = source.LocalDir;
        target.StorageTier = source.StorageTier;
        target.ImageCount = source.ImageCount;
        target.VideoCount = source.VideoCount;
        target.InvalidVideoCount = source.InvalidVideoCount;
        target.TotalBytes = source.TotalBytes;
        target.LastScanned = source.LastScanned;
        target.LastVerified = now;
        target.ExpectedImageCount = expectedImages;
        target.ExpectedVideoCount = expectedVideos;
        target.ExpectedTotalBytes = expectedBytes;
        var partial = source.ImageCount < expectedImages || sourceVideos < expectedVideos;
        target.Availability = source.InvalidVideoCount > 0
            ? CatalogStatuses.Corrupt
            : partial
                ? CatalogStatuses.Partial
                : CatalogStatuses.Available;
        target.AvailabilityReason = source.InvalidVideoCount > 0
            ? $"检测到 {source.InvalidVideoCount} 个损坏视频"
            : partial
                ? $"当前 {source.ImageCount} 图 {sourceVideos} 视，历史完整记录为 " +
                  $"{expectedImages} 图 {expectedVideos} 视"
                : "";
        target.MissingSince = "";
        if (source.InvalidVideoCount == 0 && !partial)
            target.LastComplete = now;
        CopySourceFields(target, source);
    }

    private static void SetUnavailable(LocalStat item, string status, string reason, string now)
    {
        item.Availability = status;
        item.AvailabilityReason = reason;
        if (status is CatalogStatuses.Missing or CatalogStatuses.Deleted &&
            string.IsNullOrWhiteSpace(item.MissingSince))
            item.MissingSince = now;
    }

    private static void NormalizeStatus(LocalStat item)
    {
        EnsureExpectedCounts(item);
        if (string.IsNullOrWhiteSpace(item.Availability) ||
            item.Availability.Equals(CatalogStatuses.Unverified, StringComparison.OrdinalIgnoreCase))
        {
            item.Availability = item.InvalidVideoCount > 0
                ? CatalogStatuses.Corrupt
                : CatalogStatuses.Unverified;
        }
    }

    private static void CopyCatalogFields(LocalStat target, LocalStat source)
    {
        target.SetId = source.SetId;
        target.SourcePostId = source.SourcePostId;
        target.SourceUrl = source.SourceUrl;
        target.PanUrl = source.PanUrl;
        target.PanPassword = source.PanPassword;
        target.ExtractPassword = source.ExtractPassword;
        target.Category = source.Category;
        target.Model = source.Model;
        target.Title = source.Title;
        target.LocalDir = source.LocalDir;
        target.StorageTier = source.StorageTier;
        target.ImageCount = source.ImageCount;
        target.VideoCount = source.VideoCount;
        target.InvalidVideoCount = source.InvalidVideoCount;
        target.TotalBytes = source.TotalBytes;
        target.ExpectedImageCount = source.ExpectedImageCount;
        target.ExpectedVideoCount = source.ExpectedVideoCount;
        target.ExpectedTotalBytes = source.ExpectedTotalBytes;
        target.LastScanned = source.LastScanned;
        target.Availability = source.Availability;
        target.AvailabilityReason = source.AvailabilityReason;
        target.LastVerified = source.LastVerified;
        target.LastComplete = source.LastComplete;
        target.MissingSince = source.MissingSince;
        target.MergedParts = CloneMergedParts(source.MergedParts);
    }

    private static LocalStat Clone(LocalStat item) => new()
    {
        SetId = item.SetId,
        SourcePostId = item.SourcePostId,
        SourceUrl = item.SourceUrl,
        PanUrl = item.PanUrl,
        PanPassword = item.PanPassword,
        ExtractPassword = item.ExtractPassword,
        Category = item.Category,
        Model = item.Model,
        Title = item.Title,
        LocalDir = item.LocalDir,
        StorageTier = item.StorageTier,
        ImageCount = item.ImageCount,
        VideoCount = item.VideoCount,
        InvalidVideoCount = item.InvalidVideoCount,
        TotalBytes = item.TotalBytes,
        ExpectedImageCount = item.ExpectedImageCount,
        ExpectedVideoCount = item.ExpectedVideoCount,
        ExpectedTotalBytes = item.ExpectedTotalBytes,
        LastScanned = item.LastScanned,
        Availability = item.Availability,
        AvailabilityReason = item.AvailabilityReason,
        LastVerified = item.LastVerified,
        LastComplete = item.LastComplete,
        MissingSince = item.MissingSince,
        MergedParts = CloneMergedParts(item.MergedParts)
    };

    private static List<MergedPartInfo> CloneMergedParts(IEnumerable<MergedPartInfo>? parts) =>
        (parts ?? []).Select(part => new MergedPartInfo
        {
            SourceSetId = part.SourceSetId,
            Title = part.Title,
            RelativeDirectory = part.RelativeDirectory,
            SourceUrl = part.SourceUrl,
            PanUrl = part.PanUrl,
            PanPassword = part.PanPassword,
            ExtractPassword = part.ExtractPassword
        }).ToList();

    private static void EnsureExpectedCounts(LocalStat item)
    {
        item.ExpectedImageCount = Math.Max(item.ExpectedImageCount, item.ImageCount);
        item.ExpectedVideoCount = Math.Max(
            item.ExpectedVideoCount,
            item.VideoCount + item.InvalidVideoCount);
        item.ExpectedTotalBytes = Math.Max(item.ExpectedTotalBytes, item.TotalBytes);
    }

    private static void CopySourceFields(LocalStat target, LocalStat source)
    {
        if (!string.IsNullOrWhiteSpace(source.SourcePostId))
            target.SourcePostId = source.SourcePostId;
        if (!string.IsNullOrWhiteSpace(source.SourceUrl))
            target.SourceUrl = source.SourceUrl;
        if (!string.IsNullOrWhiteSpace(source.PanUrl))
            target.PanUrl = source.PanUrl;
        if (!string.IsNullOrWhiteSpace(source.PanPassword))
            target.PanPassword = source.PanPassword;
        if (!string.IsNullOrWhiteSpace(source.ExtractPassword))
            target.ExtractPassword = source.ExtractPassword;
    }

    private static SourceMetadataIndex BuildSourceMetadataIndex(IEnumerable<ResourceItem> resources)
    {
        var ordered = resources
            .OrderByDescending(resource => !string.IsNullOrWhiteSpace(resource.PanUrl))
            .ThenByDescending(resource => !string.IsNullOrWhiteSpace(resource.DetailUrl))
            .ToArray();
        var byPath = ordered
            .Where(resource => !string.IsNullOrWhiteSpace(resource.LocalDir))
            .GroupBy(resource => NormalizePath(resource.LocalDir), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byTitle = ordered
            .GroupBy(resource => SourceTitleKey(resource.Model, resource.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return new SourceMetadataIndex(byPath, byTitle);
    }

    private static void EnrichSourceMetadata(LocalStat item, SourceMetadataIndex index)
    {
        var localDir = NormalizePath(item.LocalDir);
        ResourceItem? source = null;
        if (!string.IsNullOrWhiteSpace(localDir))
            index.ByPath.TryGetValue(localDir, out source);
        source ??= index.ByTitle.GetValueOrDefault(SourceTitleKey(item.Model, item.Title));
        if (source == null) return;
        CopySourceFields(item, new LocalStat
        {
            SourcePostId = source.PostId,
            SourceUrl = source.DetailUrl,
            PanUrl = source.PanUrl,
            PanPassword = source.PanPassword,
            ExtractPassword = source.ExtractPassword
        });
    }

    private static string? FindFirstCoverSource(
        LocalStat item,
        Settings settings,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(item.LocalDir) || !Directory.Exists(item.LocalDir))
            return null;
        var imageExts = settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? video = null;
        var checkedFiles = 0;
        foreach (var path in Directory.EnumerateFiles(item.LocalDir, "*", SearchOption.AllDirectories))
        {
            if ((checkedFiles++ & 31) == 0)
                token.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            if (imageExts.Contains(extension) &&
                MediaFileValidator.QuickImageHeaderLooksValid(path))
                return path;
            if (video == null && videoExts.Contains(extension) &&
                VideoValidator.QuickHeaderLooksValid(path))
                video = path;
        }
        return video;
    }

    private static string SourceTitleKey(string model, string title) =>
        model.Trim() + "\u001f" + title.Trim();

    private static bool InScope(LocalStat item, IReadOnlySet<string>? modelScope) =>
        modelScope == null || modelScope.Contains(ModelKey(item));

    internal static string ModelKey(LocalStat item) =>
        LibraryPaths.NormalizeCategory(item.Category) + "|" + XiurenClient.Safe(item.Model);

    private static bool IsArchive(LocalStat item) =>
        item.StorageTier.Equals(StorageTiers.Archive, StringComparison.OrdinalIgnoreCase);

    private static bool IsStatus(LocalStat item, string status) =>
        item.Availability.Equals(status, StringComparison.OrdinalIgnoreCase);

    private string DetectStorageTier(string path)
    {
        var archive = NormalizePath(state.Settings.ArchiveRoot);
        var candidate = NormalizePath(path);
        return !string.IsNullOrWhiteSpace(archive) &&
               (candidate.Equals(archive, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(archive + "\\", StringComparison.OrdinalIgnoreCase))
            ? StorageTiers.Archive
            : StorageTiers.Local;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.Trim(); }
    }

    private static string NewSetId() => Guid.NewGuid().ToString("N");

    private static string CatalogFingerprint(IEnumerable<LocalStat> items)
    {
        var ordered = items
            .OrderBy(item => item.SetId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ordered, Settings.JsonOptions))));
    }

    private static string NaturalSortKey(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            value,
            @"\d+",
            match => match.Value.PadLeft(16, '0'));
}
