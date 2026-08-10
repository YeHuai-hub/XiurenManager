using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiurenDownloader;

namespace XiurenManager;

internal sealed class StorageMigrationService : IDisposable
{
    private static readonly string StateFile = Path.Combine(
        AppPaths.DataDir,
        "storage-migration-state.json");
    private static readonly string MigrationLog = Path.Combine(
        AppPaths.LogDir,
        "storage-migration.log");
    private static readonly string[] IncompletePatterns =
    [
        "*.BaiduPCS-Go-downloading", "*.aria2", "*.part", "*.download", "*.tmp"
    ];

    private readonly AppState state;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource lifetime = new();
    private CancellationTokenSource? activeBatch;
    private Task? loop;
    private int running;

    public bool IsRunning => Volatile.Read(ref running) == 1;
    public event EventHandler? StatusChanged;

    public StorageMigrationService(AppState state)
    {
        this.state = state;
    }

    public void Start()
    {
        if (loop != null) return;
        loop = Task.Run(AutoLoopAsync);
    }

    public void Pause()
    {
        state.Settings.StorageManagementEnabled = false;
        state.Settings.Save();
        activeBatch?.Cancel();
        UpdateStatus(LoadStatus() with
        {
            Status = "Paused",
            LastError = "",
            LastRunAt = DateTime.Now.ToString("s")
        });
        WriteLog("自动存储整理已暂停；正在复制的单个文件完成后会安全停止。");
    }

    public void Resume()
    {
        state.Settings.StorageManagementEnabled = true;
        state.Settings.Save();
        if (IsRunning)
        {
            WriteLog("存储迁移已在运行，继续保持当前进度。");
            RaiseStatusChanged();
            return;
        }
        UpdateStatus(LoadStatus() with
        {
            Status = "Pending",
            LastError = "",
            LastRunAt = DateTime.Now.ToString("s")
        });
        WriteLog("自动存储整理已启用。");
        _ = Task.Run(() => RunBatchAsync(manual: true));
    }

    public void YieldForDownloads()
    {
        if (!IsRunning) return;
        activeBatch?.Cancel();
        WriteLog("下载任务即将开始，存储迁移正在安全让路。");
    }

    public void TriggerSoon()
    {
        if (!state.Settings.StorageManagementEnabled || IsRunning) return;
        _ = Task.Run(() => RunBatchAsync(manual: false));
    }

    public async Task<StorageMigrationStatus> RunBatchAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        if (!manual && !state.Settings.StorageManagementEnabled)
            return LoadStatus();
        if (!await gate.WaitAsync(0, cancellationToken))
            return LoadStatus();

        FileStream? processLock = null;
        Volatile.Write(ref running, 1);
        activeBatch = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        var token = activeBatch.Token;
        try
        {
            using var operationLease = await ResourceOperationLock.AcquireAsync(token);
            processLock = TryAcquireProcessLock();
            if (processLock == null)
            {
                WriteLog("另一个程序实例正在执行存储迁移，本实例保持等待。");
                return GetStatus();
            }
            if (state.Queue.IsRunning)
                return SetIdle("WaitingForDownloads", "下载队列运行中，本轮暂不迁移。");

            var settings = state.Settings.Snapshot();
            ValidateSettings(settings);
            if (!Directory.Exists(settings.ArchiveRoot))
                return SetIdle("NasOffline", "NAS 当前离线，迁移队列已保留。");

            await RecoverInterruptedAsync(settings, token);
            if (state.Queue.IsRunning)
                return SetIdle("WaitingForDownloads", "下载队列运行中，本轮暂不迁移。");

            var inventory = BuildInventory(settings);
            var splitModels = inventory.Where(x => x.IsSplit).Select(x => x.Model).ToArray();
            if (splitModels.Length > 0)
                WriteLog("发现跨存储重复模特，已跳过: " + string.Join("、", splitModels.Take(10)));

            var localSpaceBefore = DiskSpace(settings.DownloadRoot);
            var desiredLocal = DesiredLocalModels(
                inventory,
                settings,
                localSpaceBefore.FreeBytes);
            var batchLimit = Gb(settings.MigrationBatchGB);
            long movedBytes = 0;
            var movedModels = 0;
            var blocked = 0;

            var demotions = inventory
                .Where(x => !x.IsArchive && !x.IsSplit && !desiredLocal.Contains(x.Model))
                .OrderBy(x => x.Bytes > batchLimit)
                .ThenBy(x => x.Pinned)
                .ThenBy(x => x.Score)
                .ThenBy(x => x.FavoriteUpdatedAt)
                .ThenByDescending(x => x.Bytes)
                .ToArray();

            foreach (var model in demotions)
            {
                token.ThrowIfCancellationRequested();
                if (movedModels > 0 && movedBytes >= batchLimit) break;
                if (movedModels > 0 && movedBytes + model.Bytes > batchLimit) break;
                if (HasIncompleteFiles(model.Directory))
                {
                    blocked++;
                    WriteLog($"跳过未完成模特: {model.Model}");
                    continue;
                }

                var archiveSpace = DiskSpace(settings.ArchiveRoot);
                if (!archiveSpace.Available ||
                    archiveSpace.FreeBytes - model.Bytes < Gb(settings.ArchiveReserveGB))
                {
                    return FinishBatch(
                        "ArchiveReserveReached",
                        movedModels,
                        movedBytes,
                        blocked,
                        settings,
                        "NAS 已达到保留空间下限，本轮停止迁移。");
                }

                await MoveModelAsync(model, toArchive: true, settings, token);
                movedModels++;
                movedBytes += model.Bytes;
            }

            var refreshed = BuildInventory(settings);
            var promotions = refreshed
                .Where(x => x.IsArchive && !x.IsSplit && desiredLocal.Contains(x.Model))
                .OrderByDescending(x => x.Pinned)
                .ThenByDescending(x => x.Score)
                .ThenByDescending(x => x.FavoriteUpdatedAt)
                .ToArray();
            foreach (var model in promotions)
            {
                token.ThrowIfCancellationRequested();
                if (movedModels > 0 && movedBytes >= batchLimit) break;
                if (movedModels > 0 && movedBytes + model.Bytes > batchLimit) break;
                var localSpace = DiskSpace(settings.DownloadRoot);
                if (!localSpace.Available ||
                    localSpace.FreeBytes - model.Bytes < Gb(settings.LocalReserveGB))
                    break;
                if (HasIncompleteFiles(model.Directory))
                {
                    blocked++;
                    continue;
                }
                await MoveModelAsync(model, toArchive: false, settings, token);
                movedModels++;
                movedBytes += model.Bytes;
            }

            return FinishBatch(
                movedModels == 0 ? "Balanced" : "Running",
                movedModels,
                movedBytes,
                blocked,
                settings,
                movedModels == 0 ? "当前存储布局无需调整。" : "本批迁移完成。");
        }
        catch (OperationCanceledException)
        {
            return SetIdle("Paused", "迁移已安全暂停，可稍后继续。");
        }
        catch (Exception ex)
        {
            var current = LoadStatus();
            var failed = current with
            {
                Status = "Failed",
                LastError = ex.Message,
                LastRunAt = DateTime.Now.ToString("s")
            };
            UpdateStatus(failed);
            WriteLog("迁移失败: " + ex);
            return failed;
        }
        finally
        {
            processLock?.Dispose();
            activeBatch.Dispose();
            activeBatch = null;
            Volatile.Write(ref running, 0);
            gate.Release();
            RaiseStatusChanged();
        }
    }

    public StorageMigrationStatus GetStatus()
    {
        var current = LoadStatus();
        var local = DiskSpace(state.Settings.DownloadRoot);
        var archive = DiskSpace(state.Settings.ArchiveRoot);
        return current with
        {
            Enabled = state.Settings.StorageManagementEnabled,
            LocalFreeBytes = local.FreeBytes,
            LocalTotalBytes = local.TotalBytes,
            ArchiveOnline = archive.Available,
            ArchiveFreeBytes = archive.FreeBytes,
            ArchiveTotalBytes = archive.TotalBytes
        };
    }

    public async Task<StorageMigrationStatus> MoveNamedModelAsync(
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) return LoadStatus();
        FileStream? processLock = null;
        Volatile.Write(ref running, 1);
        activeBatch = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.Token,
            cancellationToken);
        try
        {
            using var operationLease = await ResourceOperationLock.AcquireAsync(
                activeBatch.Token);
            if (state.Queue.IsRunning)
                throw new InvalidOperationException("下载队列运行中，指定迁移未启动。");
            processLock = TryAcquireProcessLock();
            if (processLock == null)
            {
                WriteLog("另一个程序实例正在执行存储迁移，指定迁移未启动。");
                return GetStatus();
            }
            var settings = state.Settings.Snapshot();
            ValidateSettings(settings);
            if (!Directory.Exists(settings.ArchiveRoot))
                throw new IOException("NAS 当前离线。");
            var matches = BuildInventory(settings).Where(x =>
                    x.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || matches[0].IsSplit)
                throw new InvalidOperationException("找不到唯一且未拆分的模特目录: " + modelName);
            var model = matches[0];
            if (HasIncompleteFiles(model.Directory))
                throw new InvalidOperationException("该模特包含未完成下载文件，不能迁移。");
            var destinationSpace = DiskSpace(
                model.IsArchive ? settings.DownloadRoot : settings.ArchiveRoot);
            var reserve = Gb(model.IsArchive
                ? settings.LocalReserveGB
                : settings.ArchiveReserveGB);
            if (!destinationSpace.Available || destinationSpace.FreeBytes - model.Bytes < reserve)
                throw new IOException("目标存储空间低于保留线，不能迁移。");
            await MoveModelAsync(model, !model.IsArchive, settings, activeBatch.Token);
            return FinishBatch(
                "Completed", 1, model.Bytes, 0, settings,
                "指定模特迁移完成。");
        }
        catch (Exception ex)
        {
            var failed = LoadStatus() with
            {
                Status = ex is OperationCanceledException ? "Paused" : "Failed",
                LastError = ex.Message,
                LastRunAt = DateTime.Now.ToString("s")
            };
            UpdateStatus(failed);
            WriteLog("指定模特迁移失败: " + ex.Message);
            return failed;
        }
        finally
        {
            processLock?.Dispose();
            activeBatch.Dispose();
            activeBatch = null;
            Volatile.Write(ref running, 0);
            gate.Release();
            RaiseStatusChanged();
        }
    }

    private async Task AutoLoopAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                if (state.Settings.StorageManagementEnabled)
                    await RunBatchAsync(manual: false, lifetime.Token);
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Clamp(state.Settings.StorageCheckMinutes, 2, 1440)),
                    lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task MoveModelAsync(
        ModelStorage model,
        bool toArchive,
        Settings settings,
        CancellationToken token)
    {
        var destination = toArchive
            ? LibraryPaths.ArchiveModelRoot(settings, model.Category, model.Model)
            : LibraryPaths.ModelRoot(settings, model.Category, model.Model);
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidOperationException("目标资源库路径未配置。");
        if (Directory.Exists(destination))
            throw new InvalidOperationException($"目标已存在同名模特目录: {destination}");

        var parent = Directory.GetParent(destination)?.FullName ??
                     throw new InvalidOperationException("无法确定迁移目标目录。");
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, "." + Path.GetFileName(destination) + ".xiuren-migrating");
        Directory.CreateDirectory(temp);
        var sourceFiles = Directory.EnumerateFiles(
                model.Directory,
                "*",
                SearchOption.AllDirectories)
            .Where(x => !AppPaths.IsInsideTool(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalBytes = sourceFiles.Sum(x => new FileInfo(x).Length);
        UpdateStatus(LoadStatus() with
        {
            Status = "Copying",
            CurrentModel = model.Model,
            Direction = toArchive ? "LocalToNas" : "NasToLocal",
            Phase = "Copying",
            SourcePath = model.Directory,
            DestinationPath = destination,
            TempPath = temp,
            CurrentFiles = 0,
            TotalFiles = sourceFiles.Length,
            CurrentBytes = 0,
            TotalBytes = totalBytes,
            LastRunAt = DateTime.Now.ToString("s"),
            LastError = ""
        });
        WriteLog($"开始整模特迁移: {model.Model} | {model.Directory} -> {destination}");

        var completedFiles = 0;
        long transferredBytes = 0;
        var progressLock = new object();
        var lastProgressAt = DateTime.MinValue;

        void ReportProgress(long delta, bool fileCompleted = false)
        {
            lock (progressLock)
            {
                transferredBytes += delta;
                if (fileCompleted) completedFiles++;
                if (DateTime.UtcNow - lastProgressAt < TimeSpan.FromSeconds(2) &&
                    transferredBytes < totalBytes)
                    return;
                lastProgressAt = DateTime.UtcNow;
                UpdateStatus(LoadStatus() with
                {
                    Status = "Copying",
                    Phase = "Copying",
                    CurrentFiles = completedFiles,
                    TotalFiles = sourceFiles.Length,
                    CurrentBytes = Math.Min(totalBytes, transferredBytes),
                    TotalBytes = totalBytes,
                    LastRunAt = DateTime.Now.ToString("s")
                });
            }
        }

        await Parallel.ForEachAsync(
            sourceFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = settings.MigrationParallelism,
                CancellationToken = token
            },
            async (sourceFile, fileToken) =>
            {
                var relative = Path.GetRelativePath(model.Directory, sourceFile);
                var targetFile = Path.Combine(temp, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                await CopyAndVerifyAsync(
                    sourceFile,
                    targetFile,
                    fileToken,
                    delta => ReportProgress(delta));
                ReportProgress(0, fileCompleted: true);
            });
        RemoveDestinationExtras(model.Directory, temp);
        UpdateStatus(LoadStatus() with
        {
            CurrentFiles = completedFiles,
            TotalFiles = sourceFiles.Length,
            CurrentBytes = transferredBytes,
            TotalBytes = totalBytes
        });
        VerifyTree(model.Directory, temp);

        UpdateStatus(LoadStatus() with { Phase = "Verified", Status = "Verifying" });
        Directory.Move(temp, destination);
        UpdateStatus(LoadStatus() with { Phase = "Finalized", Status = "Committing" });

        UpdateTrackedPaths(model.Model, model.Directory, destination);
        state.Database.Save();
        state.Favorites.UpdateModelLocations(model.Model, model.Directory, destination);
        state.Metadata.QueueSync(state.Database.LocalFiles.Where(item =>
            item.Model.Equals(model.Model, StringComparison.OrdinalIgnoreCase)));
        UpdateStatus(LoadStatus() with { Phase = "CleanupReady", Status = "CleaningSource" });

        await DeleteVerifiedSourceAsync(model.Directory, token);
        RemoveEmptyParents(model.Directory, settings);
        UpdateStatus(LoadStatus() with
        {
            Phase = "Completed",
            Status = "Running",
            CurrentModel = "",
            SourcePath = "",
            DestinationPath = "",
            TempPath = ""
        });
        WriteLog($"整模特迁移完成: {model.Model} ({FormatBytes(model.Bytes)})");
    }

    private async Task RecoverInterruptedAsync(Settings settings, CancellationToken token)
    {
        var status = LoadStatus();
        if (string.IsNullOrWhiteSpace(status.SourcePath) ||
            string.IsNullOrWhiteSpace(status.DestinationPath))
            return;

        if (Directory.Exists(status.DestinationPath) && Directory.Exists(status.SourcePath) &&
            status.Phase is "Verified" or "Finalized" or "DatabaseUpdated" or "CleanupReady")
        {
            if (status.Phase == "DatabaseUpdated")
            {
                await VerifyRemainingSourceAsync(
                    status.SourcePath,
                    status.DestinationPath,
                    token);
            }
            else
            {
                VerifyTree(status.SourcePath, status.DestinationPath);
            }
            var model = Path.GetFileName(status.SourcePath);
            UpdateTrackedPaths(model, status.SourcePath, status.DestinationPath);
            state.Database.Save();
            state.Favorites.UpdateModelLocations(model, status.SourcePath, status.DestinationPath);
            state.Metadata.QueueSync(state.Database.LocalFiles.Where(item =>
                item.Model.Equals(model, StringComparison.OrdinalIgnoreCase)));
            status = status with { Phase = "CleanupReady", Status = "CleaningSource" };
            UpdateStatus(status);
            token.ThrowIfCancellationRequested();
            await DeleteVerifiedSourceAsync(status.SourcePath, token);
            RemoveEmptyParents(status.SourcePath, settings);
            UpdateStatus(status with
            {
                Phase = "Completed",
                Status = "Pending",
                CurrentModel = "",
                SourcePath = "",
                DestinationPath = "",
                TempPath = "",
                LastError = ""
            });
            WriteLog($"已恢复并完成上次中断迁移: {model}");
            return;
        }

        if (!Directory.Exists(status.SourcePath))
        {
            UpdateStatus(status with
            {
                Phase = "Completed",
                Status = "Pending",
                CurrentModel = "",
                SourcePath = "",
                DestinationPath = "",
                TempPath = ""
            });
        }
    }

    private List<ModelStorage> BuildInventory(Settings settings)
    {
        var pinned = settings.PinnedLocalModels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var splitModels = PhysicalModelDirectories(settings)
            .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(x => x.Directory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = state.Database.LocalFiles
            .Where(x => Directory.Exists(x.LocalDir))
            .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModelStorage>();
        foreach (var group in groups)
        {
            var roots = group.Select(item => new
                {
                    Item = item,
                    IsArchive = LibraryPaths.IsInside(item.LocalDir, settings.ArchiveRoot),
                    ModelDir = Directory.GetParent(item.LocalDir)?.FullName ?? ""
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.ModelDir))
                .GroupBy(x => x.ModelDir, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var priority = state.Favorites.GetModelPriority(group.Key);
            foreach (var root in roots)
            {
                var items = root.Select(x => x.Item).ToArray();
                result.Add(new ModelStorage(
                    group.Key,
                    items.Select(x => x.Category).FirstOrDefault() ?? LibraryPaths.DefaultCategory,
                    root.Key,
                    root.First().IsArchive,
                    Math.Max(1, items.Sum(x => x.TotalBytes)),
                    priority.Score,
                    priority.UpdatedAt,
                    pinned.Contains(group.Key),
                    roots.Length > 1 || splitModels.Contains(group.Key)));
            }
        }
        return result;
    }

    private static HashSet<string> DesiredLocalModels(
        IReadOnlyCollection<ModelStorage> inventory,
        Settings settings,
        long localFreeBytes)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentLocalBytes = inventory
            .Where(x => !x.IsArchive)
            .Select(x => new { x.Directory, x.Bytes })
            .DistinctBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
            .Sum(x => x.Bytes);
        var reserveShortfall = Math.Max(0, Gb(settings.LocalReserveGB) - localFreeBytes);
        var reserveBoundBudget = Math.Max(0, currentLocalBytes - reserveShortfall);
        var effectiveBudget = Math.Min(Gb(settings.LocalHotBudgetGB), reserveBoundBudget);
        long used = 0;
        foreach (var model in inventory
                     .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderByDescending(x => x.Pinned)
                         .ThenByDescending(x => x.Score)
                         .First())
                     .OrderByDescending(x => x.Pinned)
                     .ThenByDescending(x => x.Score)
                     .ThenByDescending(x => x.FavoriteUpdatedAt)
                     .ThenBy(x => x.Model, StringComparer.OrdinalIgnoreCase))
        {
            if (used + model.Bytes > effectiveBudget) continue;
            desired.Add(model.Model);
            used += model.Bytes;
        }
        return desired;
    }

    private static IEnumerable<(string Model, string Directory)> PhysicalModelDirectories(
        Settings settings)
    {
        foreach (var root in new[] { settings.DownloadRoot, settings.ArchiveRoot })
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            string[] categories;
            try
            {
                categories = Directory.EnumerateDirectories(root).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (var category in categories)
            {
                string[] models;
                try
                {
                    models = Directory.EnumerateDirectories(category)
                        .Where(x => !Path.GetFileName(x).StartsWith('.'))
                        .ToArray();
                }
                catch
                {
                    continue;
                }
                foreach (var directory in models)
                    yield return (Path.GetFileName(directory), directory);
            }
        }
    }

    private void UpdateTrackedPaths(string model, string source, string destination)
    {
        foreach (var item in state.Database.LocalFiles.Where(x =>
                     x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                     LibraryPaths.IsInside(x.LocalDir, source)))
        {
            item.LocalDir = Path.Combine(destination, Path.GetRelativePath(source, item.LocalDir));
            item.StorageTier = LibraryPaths.StorageTier(state.Settings, item.LocalDir);
        }
        foreach (var item in state.Database.Resources.Where(x =>
                     x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                     LibraryPaths.IsInside(x.LocalDir, source)))
        {
            item.LocalDir = Path.Combine(destination, Path.GetRelativePath(source, item.LocalDir));
        }
    }

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        CancellationToken token,
        Action<long>? reportBytes = null)
    {
        var sourceInfo = new FileInfo(source);
        if (File.Exists(destination) && new FileInfo(destination).Length == sourceInfo.Length)
        {
            var existingSourceHash = await HashAsync(source, token);
            var existingDestinationHash = await HashAsync(destination, token);
            if (CryptographicOperations.FixedTimeEquals(existingSourceHash, existingDestinationHash))
            {
                reportBytes?.Invoke(sourceInfo.Length);
                return;
            }
        }

        var temp = destination + ".copying";
        byte[] sourceHash;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        await using (var input = new FileStream(
                         source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(
                         temp, FileMode.Create, FileAccess.Write, FileShare.None,
                         4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[4 * 1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                hash.AppendData(buffer, 0, read);
                reportBytes?.Invoke(read);
            }
            await output.FlushAsync(token);
            sourceHash = hash.GetHashAndReset();
        }
        File.Move(temp, destination, true);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
        var destinationHash = await HashAsync(destination, token);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            throw new IOException("复制校验失败: " + source);
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, token);
    }

    private static void VerifyTree(string source, string destination)
    {
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(x => Path.GetRelativePath(source, x), x => new FileInfo(x).Length,
                StringComparer.OrdinalIgnoreCase);
        var destinationFiles = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)
            .Where(x => !x.EndsWith(".copying", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => Path.GetRelativePath(destination, x), x => new FileInfo(x).Length,
                StringComparer.OrdinalIgnoreCase);
        if (sourceFiles.Count != destinationFiles.Count ||
            sourceFiles.Any(x => !destinationFiles.TryGetValue(x.Key, out var length) || length != x.Value))
            throw new IOException("目录校验失败，源目录仍会保留: " + source);
    }

    private static void RemoveDestinationExtras(string source, string destination)
    {
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(source, x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var destinationFile in Directory.EnumerateFiles(
                     destination,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (destinationFile.EndsWith(".copying", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destinationFile);
                continue;
            }
            var relative = Path.GetRelativePath(destination, destinationFile);
            if (!sourceFiles.Contains(relative))
                File.Delete(destinationFile);
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     destination,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(x => x.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static async Task VerifyRemainingSourceAsync(
        string source,
        string destination,
        CancellationToken token)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, sourceFile);
            var destinationFile = Path.Combine(destination, relative);
            if (!File.Exists(destinationFile) ||
                new FileInfo(sourceFile).Length != new FileInfo(destinationFile).Length)
            {
                throw new IOException(
                    "恢复清理校验失败，源目录仍会保留: " + sourceFile);
            }

            var sourceHash = await HashAsync(sourceFile, token);
            var destinationHash = await HashAsync(destinationFile, token);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
            {
                throw new IOException(
                    "恢复清理校验失败，文件内容不同，源目录仍会保留: " + sourceFile);
            }
        }
    }

    private static async Task DeleteVerifiedSourceAsync(
        string source,
        CancellationToken token)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(source)) return;
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                             source,
                             "*",
                             SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                File.SetAttributes(source, FileAttributes.Normal);
                Directory.Delete(source, recursive: true);
                return;
            }
            catch (Exception ex) when (
                attempt < 5 &&
                ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
            }
        }
    }

    private static bool HasIncompleteFiles(string directory)
    {
        foreach (var pattern in IncompletePatterns)
        {
            if (Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Any())
                return true;
        }
        return false;
    }

    private static FileStream? TryAcquireProcessLock()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            return new FileStream(
                Path.Combine(AppPaths.DataDir, "storage-migration.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateSettings(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ArchiveRoot))
            throw new InvalidOperationException("尚未设置 NAS 资源库路径。");
        if (LibraryPaths.IsInside(settings.DownloadRoot, settings.ArchiveRoot) ||
            LibraryPaths.IsInside(settings.ArchiveRoot, settings.DownloadRoot) ||
            settings.DownloadRoot.Equals(settings.ArchiveRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地资源库与 NAS 资源库不能互相包含。");
    }

    private StorageMigrationStatus FinishBatch(
        string status,
        int movedModels,
        long movedBytes,
        int blocked,
        Settings settings,
        string message)
    {
        var local = DiskSpace(settings.DownloadRoot);
        var archive = DiskSpace(settings.ArchiveRoot);
        var value = LoadStatus() with
        {
            Status = status,
            Phase = "Completed",
            CurrentModel = "",
            SourcePath = "",
            DestinationPath = "",
            TempPath = "",
            LastRunAt = DateTime.Now.ToString("s"),
            LastError = "",
            LastBatchModels = movedModels,
            LastBatchBytes = movedBytes,
            TotalMovedModels = LoadStatus().TotalMovedModels + movedModels,
            TotalMovedBytes = LoadStatus().TotalMovedBytes + movedBytes,
            BlockedModels = blocked,
            Enabled = settings.StorageManagementEnabled,
            LocalFreeBytes = local.FreeBytes,
            LocalTotalBytes = local.TotalBytes,
            ArchiveOnline = archive.Available,
            ArchiveFreeBytes = archive.FreeBytes,
            ArchiveTotalBytes = archive.TotalBytes
        };
        UpdateStatus(value);
        WriteLog($"{message} 模特 {movedModels} 个，数据 {FormatBytes(movedBytes)}，阻塞 {blocked} 个。");
        if (movedModels > 0)
        {
            state.NotifyDataChanged();
        }
        return value;
    }

    private StorageMigrationStatus SetIdle(string status, string message)
    {
        var value = GetStatus() with
        {
            Status = status,
            LastRunAt = DateTime.Now.ToString("s"),
            LastError = ""
        };
        UpdateStatus(value);
        WriteLog(message);
        return value;
    }

    private static StorageMigrationStatus LoadStatus()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<StorageMigrationStatus>(
                      File.ReadAllText(StateFile, Encoding.UTF8),
                      Settings.JsonOptions) ?? new StorageMigrationStatus()
                : new StorageMigrationStatus();
        }
        catch
        {
            return new StorageMigrationStatus();
        }
    }

    private void UpdateStatus(StorageMigrationStatus value)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var temp = StateFile + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(value, Settings.JsonOptions),
            Encoding.UTF8);
        File.Move(temp, StateFile, true);
        RaiseStatusChanged();
    }

    private void WriteLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            File.AppendAllText(MigrationLog, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
        state.WriteLog(message);
    }

    private void RaiseStatusChanged()
    {
        var dispatcher = App.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private static void RemoveEmptyParents(string source, Settings settings)
    {
        var parent = Directory.GetParent(source)?.FullName;
        foreach (var root in new[] { settings.DownloadRoot, settings.ArchiveRoot })
        {
            if (string.IsNullOrWhiteSpace(parent) || !LibraryPaths.IsInside(parent, root)) continue;
            try
            {
                if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch { }
        }
    }

    private static DiskSpaceInfo DiskSpace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new DiskSpaceInfo(false, 0, 0);
        try
        {
            if (!GetDiskFreeSpaceEx(path, out var free, out var total, out _))
                return new DiskSpaceInfo(false, 0, 0);
            return new DiskSpaceInfo(true, (long)free, (long)total);
        }
        catch
        {
            return new DiskSpaceInfo(false, 0, 0);
        }
    }

    private static long Gb(int value) => (long)value * 1024 * 1024 * 1024;

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024)
            return $"{bytes / 1024d / 1024d / 1024d / 1024d:0.00} TB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024d / 1024d:0.00} GB";
        return $"{bytes / 1024d / 1024d:0.00} MB";
    }

    public void Dispose()
    {
        lifetime.Cancel();
        activeBatch?.Cancel();
        gate.Dispose();
        lifetime.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private sealed record ModelStorage(
        string Model,
        string Category,
        string Directory,
        bool IsArchive,
        long Bytes,
        int Score,
        DateTime FavoriteUpdatedAt,
        bool Pinned,
        bool IsSplit);

    private readonly record struct DiskSpaceInfo(bool Available, long FreeBytes, long TotalBytes);
}

internal sealed record StorageMigrationStatus
{
    public string Status { get; init; } = "Pending";
    public bool Enabled { get; init; }
    public string Phase { get; init; } = "";
    public string CurrentModel { get; init; } = "";
    public string Direction { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string DestinationPath { get; init; } = "";
    public string TempPath { get; init; } = "";
    public int LastBatchModels { get; init; }
    public long LastBatchBytes { get; init; }
    public long TotalMovedModels { get; init; }
    public long TotalMovedBytes { get; init; }
    public int BlockedModels { get; init; }
    public bool ArchiveOnline { get; init; }
    public long LocalFreeBytes { get; init; }
    public long LocalTotalBytes { get; init; }
    public long ArchiveFreeBytes { get; init; }
    public long ArchiveTotalBytes { get; init; }
    public int CurrentFiles { get; init; }
    public int TotalFiles { get; init; }
    public long CurrentBytes { get; init; }
    public long TotalBytes { get; init; }
    public string LastRunAt { get; init; } = "";
    public string LastError { get; init; } = "";
}
