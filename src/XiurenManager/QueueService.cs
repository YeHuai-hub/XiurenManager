using XiurenDownloader;

namespace XiurenManager;

internal sealed class QueueService
{
    private readonly AppState state;
    private CancellationTokenSource? cancellation;
    private int processing;
    private bool stopRequested;

    public bool IsRunning => Volatile.Read(ref processing) == 1;

    public QueueService(AppState state)
    {
        this.state = state;
        var interrupted = state.Database.Jobs.Where(x =>
            x.Status.Equals("Running", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var job in interrupted)
        {
            job.Status = "Canceled";
            job.Error = "应用上次退出时任务仍在运行，可点击继续队列恢复。";
            job.FinishedAt = DateTime.Now.ToString("s");
        }
        if (interrupted.Count > 0) state.Database.Save();
    }

    public JobItem Enqueue(
        string type,
        string target,
        string aliases = "",
        int pages = 999,
        int maxReady = 9999)
    {
        var job = new JobItem
        {
            Type = type,
            Target = type == "DownloadReady" ? "全部就绪项" : target.Trim(),
            Aliases = aliases.Trim(),
            Pages = Math.Max(1, pages),
            MaxReady = Math.Max(0, maxReady),
            SearchMode = state.Settings.SearchMode,
            CategoryPath = state.Settings.CategoryPath,
            Status = "Queued",
            StartedAt = DateTime.Now.ToString("s")
        };
        state.Database.Jobs.Insert(0, job);
        state.Database.Save();
        state.WriteLog("已加入任务队列: " + Label(job));
        state.NotifyJobsChanged();
        _ = Task.Run(RunAsync);
        return job;
    }

    public async Task ContinueAsync()
    {
        if (IsRunning)
        {
            state.WriteLog("任务队列正在运行。");
            return;
        }

        var database = state.Database;
        var job = database.Jobs.FirstOrDefault(x =>
            x.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase));
        if (job == null && !database.Jobs.Any(IsQueued))
            job = database.Jobs.FirstOrDefault(x =>
                x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));

        if (job != null)
        {
            if (job.Type == "SearchDownload" && HasPendingForModel(job.Target))
            {
                job.Type = "DownloadModelReady";
                state.WriteLog($"检测到“{job.Target}”已有入库链接，继续操作将直接恢复未完成下载，不再从头搜索。");
            }
            job.Status = "Queued";
            job.FinishedAt = "";
            job.Error = "";
            database.Save();
            state.WriteLog("已恢复任务: " + Label(job));
            state.NotifyJobsChanged();
        }
        else if (!database.Jobs.Any(IsQueued))
        {
            state.WriteLog("没有可继续的任务。");
            return;
        }

        await Task.Run(RunAsync);
    }

    public void Stop()
    {
        stopRequested = true;
        cancellation?.Cancel();
        state.WriteLog("正在停止当前任务，未开始的排队任务会保留。");
    }

    private async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref processing, 1, 0) != 0) return;
        stopRequested = false;
        state.NotifyJobsChanged();
        try
        {
            while (!stopRequested)
            {
                var job = state.Database.Jobs.LastOrDefault(IsQueued);
                if (job == null) break;

                cancellation = new CancellationTokenSource();
                job.Status = "Running";
                job.Error = "";
                state.Database.Save();
                state.WriteLog("开始任务: " + Label(job));
                state.NotifyJobsChanged();

                try
                {
                    await ExecuteAsync(job, cancellation.Token);
                    job.Status = "Done";
                }
                catch (OperationCanceledException)
                {
                    job.Status = "Canceled";
                    state.WriteLog("任务已停止: " + Label(job));
                }
                catch (Exception ex)
                {
                    job.Status = "Failed";
                    job.Error = ErrorText.Format(ex);
                    state.WriteLog("任务失败: " + job.Error.Replace(Environment.NewLine, " | "));
                }
                finally
                {
                    job.FinishedAt = DateTime.Now.ToString("s");
                    state.Database.Save();
                    cancellation.Dispose();
                    cancellation = null;
                    state.NotifyJobsChanged();
                }
            }
        }
        finally
        {
            Volatile.Write(ref processing, 0);
            stopRequested = false;
            state.NotifyJobsChanged();
        }
    }

    private async Task ExecuteAsync(JobItem job, CancellationToken token)
    {
        if (job.Type is "Search" or "SearchDownload")
        {
            var resources = await SearchAsync(job, token);
            state.WriteLog($"本轮入库 {resources.Count} 套");
            if (job.Type == "SearchDownload")
                await new Downloader(state.Settings, state.Database, Progress()).RunAsync(resources, token);
            if (job.Type == "SearchDownload")
                await ScanAsync(token);
            return;
        }

        if (job.Type == "DownloadReady")
        {
            RepairMissingCompleted();
            var resources = state.Database.Resources.Where(x =>
                    x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
                    !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            state.WriteLog($"下载就绪未完成项: {resources.Count} 条");
            await new Downloader(state.Settings, state.Database, Progress()).RunAsync(resources, token);
            await ScanAsync(token);
            return;
        }

        if (job.Type == "DownloadModelReady")
        {
            RepairMissingCompleted(job.Target);
            var model = XiurenClient.Safe(job.Target.Trim());
            var resources = state.Database.Resources.Where(x =>
                    x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                    x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
                    !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            state.WriteLog($"恢复“{model}”未完成下载: {resources.Count} 条");
            await new Downloader(state.Settings, state.Database, Progress()).RunAsync(resources, token);
            await ScanAsync(token);
            return;
        }

        throw new InvalidOperationException("未知任务类型: " + job.Type);
    }

    private async Task<List<ResourceItem>> SearchAsync(JobItem job, CancellationToken token)
    {
        var settings = state.Settings;
        settings.SearchMode = string.IsNullOrWhiteSpace(job.SearchMode) ? settings.SearchMode : job.SearchMode;
        settings.CategoryPath = string.IsNullOrWhiteSpace(job.CategoryPath) ? settings.CategoryPath : job.CategoryPath;
        settings.Save();

        var canonicalModel = XiurenClient.Safe(job.Target.Trim());
        var merged = new Dictionary<string, ResourceItem>(StringComparer.OrdinalIgnoreCase);

        ResourceItem? FindSaved(string detailUrl)
        {
            var saved = state.Database.Resources.FirstOrDefault(x =>
                x.DetailUrl.Equals(detailUrl, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.PanUrl));
            if (saved != null) saved.Model = canonicalModel;
            return saved;
        }

        foreach (var searchName in XiurenClient.SearchNames(job.Target, job.Aliases))
        {
            token.ThrowIfCancellationRequested();
            if (job.MaxReady > 0 && merged.Count >= job.MaxReady) break;
            var remaining = job.MaxReady > 0 ? job.MaxReady - merged.Count : 0;
            state.WriteLog($"搜索名称: {searchName} → 统一归档: {canonicalModel}");
            var found = await new XiurenClient(settings).SearchAsync(
                searchName,
                job.Pages,
                remaining,
                Progress(),
                token,
                FindSaved,
                item => SaveResource(item, canonicalModel));

            foreach (var item in found)
            {
                var stored = SaveResource(item, canonicalModel);
                merged[stored.DetailUrl] = stored;
            }
            state.Database.Save();
            state.NotifyJobsChanged();
        }
        return merged.Values.ToList();
    }

    private ResourceItem SaveResource(ResourceItem item, string model)
    {
        item.Model = model;
        var stored = state.Database.Upsert(item);
        stored.Model = model;
        state.Database.Save();
        return stored;
    }

    private void RepairMissingCompleted(string? modelFilter = null)
    {
        var model = XiurenClient.Safe((modelFilter ?? "").Trim());
        var repaired = 0;
        foreach (var item in state.Database.Resources.Where(x =>
                     x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) &&
                     (string.IsNullOrWhiteSpace(model) ||
                      x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))))
        {
            if (HasUsableLocalMedia(item)) continue;
            item.Status = "Ready";
            item.DownloadStatus = "";
            item.ExtractStatus = "";
            item.Error = "本地文件缺失或媒体损坏，等待重新下载";
            repaired++;
        }
        if (repaired == 0) return;
        state.Database.Save();
        state.WriteLog($"已把本地缺失或损坏记录改回待下载: {repaired} 条");
        state.NotifyJobsChanged();
    }

    private bool HasPendingForModel(string modelName)
    {
        var model = XiurenClient.Safe(modelName.Trim());
        return state.Database.Resources.Any(x =>
            x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
            x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
            !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.PanUrl));
    }

    private bool HasUsableLocalMedia(ResourceItem item)
    {
        var directories = new[]
        {
            item.LocalDir,
            Path.Combine(
                state.Settings.DownloadRoot,
                XiurenClient.Safe(item.Model),
                XiurenClient.Safe(item.Title))
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory) || VideoValidator.HasInvalidMarker(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (state.Settings.ImageExts.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    return true;
                if (state.Settings.VideoExts.Contains(extension, StringComparer.OrdinalIgnoreCase) &&
                    VideoValidator.QuickHeaderLooksValid(file))
                    return true;
            }
        }
        return false;
    }

    private async Task ScanAsync(CancellationToken token)
    {
        await Task.Run(() => LocalScanner.Scan(state), token);
    }

    private IProgress<string> Progress() => new Progress<string>(state.WriteLog);
    private static bool IsQueued(JobItem job) =>
        job.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase);

    public static string Label(JobItem job) => job.Type switch
    {
        "Search" => "搜索入库 - " + job.Target,
        "SearchDownload" => "搜索并下载 - " + job.Target,
        "DownloadReady" => "下载就绪项",
        "DownloadModelReady" => "恢复未完成下载 - " + job.Target,
        _ => job.Type + " - " + job.Target
    };
}
