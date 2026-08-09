using System.Collections.Concurrent;
using System.Threading.Channels;
using XiurenDownloader;

namespace XiurenManager;

internal sealed class MetadataSidecarCoordinator : IDisposable
{
    private sealed record QueueEntry(
        string? Path,
        TaskCompletionSource? Completion = null,
        bool AnnounceCompletion = false,
        bool MarkBackfillComplete = false);

    private static readonly string BackfillStateFile = Path.Combine(
        AppPaths.DataDir,
        "metadata-sidecar-v1.complete");

    private readonly AppState state;
    private readonly Channel<QueueEntry> queue = Channel.CreateUnbounded<QueueEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, LocalStat> pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;

    public MetadataSidecarCoordinator(AppState state)
    {
        this.state = state;
        worker = Task.Run(ProcessAsync);
    }

    public void QueueSync(LocalStat item)
    {
        if (string.IsNullOrWhiteSpace(item.LocalDir)) return;
        var path = NormalizePath(item.LocalDir);
        if (pending.TryAdd(path, item))
            queue.Writer.TryWrite(new QueueEntry(path));
        else
            pending[path] = item;
    }

    public void QueueSync(IEnumerable<LocalStat> items)
    {
        foreach (var item in items)
            QueueSync(item);
    }

    public Task QueueStartupBackfillAsync()
    {
        if (File.Exists(BackfillStateFile))
            return Task.CompletedTask;
        return QueueAllAsync(announce: true, markBackfillComplete: true);
    }

    public Task QueueAllAsync(
        bool announce = true,
        bool markBackfillComplete = true)
    {
        var items = state.Database.LocalFiles
            .Where(item => item.ImageCount + item.VideoCount + item.InvalidVideoCount > 0)
            .ToArray();
        QueueSync(items);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Writer.TryWrite(new QueueEntry(
            null,
            completion,
            announce,
            markBackfillComplete));
        if (announce)
            state.WriteLog($"已安排同步套图资料: {items.Length:N0} 套，后台增量处理。");
        return completion.Task;
    }

    private async Task ProcessAsync()
    {
        var checkedCount = 0;
        var written = 0;
        var skipped = 0;
        var failed = 0;
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stopping.Token))
            {
                if (state.Storage.IsRunning)
                {
                    await Task.Delay(1000, stopping.Token);
                    queue.Writer.TryWrite(entry);
                    continue;
                }

                if (entry.Path == null)
                {
                    if (entry.AnnounceCompletion)
                    {
                        state.WriteLog(
                            $"套图资料同步完成: 检查 {checkedCount:N0}，更新 {written:N0}，" +
                            $"跳过 {skipped:N0}，失败 {failed:N0}。");
                    }
                    if (entry.MarkBackfillComplete && failed == 0)
                    {
                        Directory.CreateDirectory(AppPaths.DataDir);
                        File.WriteAllText(
                            BackfillStateFile,
                            $"xiuren-set/v1 {DateTime.Now:s}");
                    }
                    checkedCount = 0;
                    written = 0;
                    skipped = 0;
                    failed = 0;
                    entry.Completion?.TrySetResult();
                    continue;
                }

                if (!pending.TryRemove(entry.Path, out var item))
                    continue;
                checkedCount++;
                try
                {
                    var result = SetMetadataSidecar.Write(
                        state.Database,
                        state.Favorites,
                        item);
                    if (result == MetadataSidecarWriteResult.Written)
                    {
                        written++;
                        await Task.Delay(10, stopping.Token);
                    }
                    else if (result == MetadataSidecarWriteResult.Skipped)
                        skipped++;
                }
                catch (Exception ex)
                {
                    failed++;
                    state.WriteLog($"套图资料写入失败: {item.LocalDir} | {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        stopping.Cancel();
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        stopping.Dispose();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }
}
