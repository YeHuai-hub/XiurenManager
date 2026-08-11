using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using XiurenDownloader;

namespace XiurenManager;

internal sealed record SetMergeResult(LocalStat Merged, IReadOnlyList<string> PartDirectories);

internal sealed class SetMergeCommand
{
    public string Title { get; set; } = "";
    public List<string> SourceDirectories { get; set; } = [];
}

internal sealed class SetMergeJournal
{
    public string Schema { get; set; } = "xiuren-set-merge/v1";
    public string Target { get; set; } = "";
    public string Staging { get; set; } = "";
    public string CreatedAt { get; set; } = DateTime.Now.ToString("s");
    public List<SetMergeJournalPart> Parts { get; set; } = [];
}

internal sealed class SetMergeJournalPart
{
    public LocalStat Source { get; set; } = new();
    public string OriginalDirectory { get; set; } = "";
    public string ChildName { get; set; } = "";
}

internal static partial class SetMergeService
{
    private const string MergedReasonPrefix = "已合并到：";
    private static readonly object JournalGate = new();
    private static string JournalPath => Path.Combine(AppPaths.DataDir, "set-merge-transaction.json");

    public static bool IsMergedHistory(LocalStat item) =>
        item.Availability.Equals(CatalogStatuses.Deleted, StringComparison.OrdinalIgnoreCase) &&
        item.AvailabilityReason.StartsWith(MergedReasonPrefix, StringComparison.Ordinal);

    public static IReadOnlyList<LocalStat> AutoOrder(IEnumerable<LocalStat> items) =>
        items.OrderBy(PartOrder)
            .ThenBy(item => NaturalKey(item.Title), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string SuggestTitle(IReadOnlyList<LocalStat> ordered)
    {
        if (ordered.Count == 0) return "合并套图";
        var value = ExplicitPartMarker().Replace(ordered[0].Title, " ");
        value = BarePartSuffix().Replace(value, " ");
        value = Regex.Replace(value, @"\s{2,}", " ").Trim(' ', '-', '_', '－', '—');
        return string.IsNullOrWhiteSpace(value) ? ordered[0].Title + " 合集" : value;
    }

    public static SetMergeResult Merge(
        AppState state,
        IReadOnlyList<LocalStat> orderedSources,
        string requestedTitle)
    {
        var sources = orderedSources.ToArray();
        ValidateSources(state, sources, requestedTitle, out var parent, out var target);

        using var operationLease = ResourceOperationLock.TryAcquire() ??
            throw new InvalidOperationException("下载、扫描或存储迁移正在使用资源库，请稍后重试。");

        var staging = Path.Combine(parent, ".merge-" + Guid.NewGuid().ToString("N"));
        var journal = new SetMergeJournal { Target = target, Staging = staging };
        for (var index = 0; index < sources.Length; index++)
        {
            var original = Path.GetFullPath(sources[index].LocalDir);
            journal.Parts.Add(new SetMergeJournalPart
            {
                Source = sources[index],
                OriginalDirectory = original,
                ChildName = $"{index + 1:00} - {SafeSegment(Path.GetFileName(original))}"
            });
        }
        WriteJournal(journal);
        var moved = new List<(string Original, string Staged)>();
        var committed = false;
        try
        {
            Directory.CreateDirectory(staging);
            for (var index = 0; index < journal.Parts.Count; index++)
            {
                var source = journal.Parts[index].OriginalDirectory;
                var staged = Path.Combine(staging, journal.Parts[index].ChildName);
                Directory.Move(source, staged);
                moved.Add((source, staged));
            }

            Directory.Move(staging, target);
            committed = true;

            var partDirectories = moved
                .Select(part => Path.Combine(target, Path.GetFileName(part.Staged)))
                .ToArray();
            var merged = CommitMergedState(state, sources, target, partDirectories);
            DeleteJournal();
            state.WriteLog($"套图合并完成: {sources.Length} 套 -> {target}");
            state.NotifyDataChanged();
            return new SetMergeResult(merged, partDirectories);
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                if (RollBackMoves(staging, moved)) DeleteJournal();
                throw;
            }
            throw new InvalidOperationException(
                $"目录已经安全合并到“{target}”，但资源账本提交失败。请不要重复合并，重新扫描媒体库即可恢复索引。",
                ex);
        }
    }

    public static void RecoverPending(AppState state)
    {
        var journal = ReadJournal();
        if (journal == null) return;
        using var operationLease = ResourceOperationLock.TryAcquire();
        if (operationLease == null)
        {
            state.WriteLog("检测到未完成套图合并，资源库正忙，将在下次启动时恢复。");
            return;
        }

        try
        {
            var partDirectories = journal.Parts
                .Select(part => Path.Combine(journal.Target, part.ChildName))
                .ToArray();
            if (Directory.Exists(journal.Target))
            {
                var alreadyCommitted = state.Catalog.Snapshot().Any(item =>
                    NormalizePath(item.LocalDir).Equals(
                        NormalizePath(journal.Target),
                        StringComparison.OrdinalIgnoreCase) &&
                    !IsMergedHistory(item));
                if (!alreadyCommitted)
                {
                    if (partDirectories.Any(path => !Directory.Exists(path)))
                        throw new InvalidOperationException("合并目标中的分卷不完整，事务记录已保留。");
                    var sources = journal.Parts.Select(part => part.Source).ToArray();
                    CommitMergedState(state, sources, journal.Target, partDirectories);
                    state.WriteLog($"已恢复并提交未完成的套图合并: {journal.Target}");
                }
                DeleteJournal();
                return;
            }

            var stagedMoves = journal.Parts
                .Select(part => (
                    part.OriginalDirectory,
                    Path.Combine(journal.Staging, part.ChildName)))
                .Where(part => Directory.Exists(part.Item2))
                .ToArray();
            if (journal.Parts.Any(part =>
                    !Directory.Exists(part.OriginalDirectory) &&
                    !Directory.Exists(Path.Combine(journal.Staging, part.ChildName))))
                throw new DirectoryNotFoundException("事务中的部分原目录和临时分卷都不存在，已保留记录等待人工检查。");
            if (!RollBackMoves(journal.Staging, stagedMoves))
                throw new IOException("部分分卷未能回到原目录，事务记录已保留以便下次继续恢复。");
            if (journal.Parts.Any(part => !Directory.Exists(part.OriginalDirectory)))
                throw new IOException("部分分卷未恢复到原目录，事务记录已保留以便下次继续恢复。");
            DeleteJournal();
            state.WriteLog("已自动回滚上次未完成的套图合并。");
        }
        catch (Exception ex)
        {
            state.WriteLog("未完成套图合并恢复失败: " + ex.Message);
        }
    }

    private static LocalStat CommitMergedState(
        AppState state,
        IReadOnlyList<LocalStat> sources,
        string target,
        IReadOnlyList<string> partDirectories)
    {
        var merged = BuildMergedStat(state, sources, target, partDirectories);
        UpdateResourceLocations(state, sources, partDirectories);
        state.Catalog.MergeSets(sources, merged, partDirectories, MergedReasonPrefix + merged.Title);
        try
        {
            state.Favorites.MergeInto(merged, sources);
        }
        catch (Exception ex)
        {
            state.WriteLog("合并套图喜爱值同步失败: " + ex.Message);
        }
        try
        {
            var mediaFiles = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Where(path => !AppPaths.IsInsideTool(path))
                .Select(path => new FileInfo(path))
                .ToArray();
            state.Catalog.RecordManifest(merged, mediaFiles);
            SetMetadataSidecar.Write(state.Database, state.Favorites, merged);
        }
        catch (Exception ex)
        {
            state.WriteLog("合并套图清单或资料文件同步失败，将在下次扫描时重建: " + ex.Message);
        }
        return merged;
    }

    private static void ValidateSources(
        AppState state,
        IReadOnlyList<LocalStat> sources,
        string requestedTitle,
        out string parent,
        out string target)
    {
        if (sources.Count < 2)
            throw new InvalidOperationException("至少选择两套写真才能合并。");
        if (string.IsNullOrWhiteSpace(requestedTitle))
            throw new InvalidOperationException("合并后的套图名称不能为空。");
        if (requestedTitle.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("合并后的名称包含 Windows 不允许的字符。");
        if (sources.Select(item => item.SetId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Count)
            throw new InvalidOperationException("合并清单中存在重复套图。");

        var first = sources[0];
        if (sources.Any(item => !item.Category.Equals(first.Category, StringComparison.OrdinalIgnoreCase) ||
                                !item.Model.Equals(first.Model, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("只能合并同一分类、同一人物下的套图。");
        if (sources.Any(item => !CatalogStatuses.CanAttemptOpen(item.Availability)))
            throw new InvalidOperationException("清单中包含已删除或已合并的历史套图。");
        if (sources.Any(item => !Directory.Exists(item.LocalDir)))
            throw new DirectoryNotFoundException("部分套图目录不存在，请先重新扫描媒体库。");

        var parents = sources.Select(item => Directory.GetParent(Path.GetFullPath(item.LocalDir))?.FullName ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (parents.Length != 1 || string.IsNullOrWhiteSpace(parents[0]))
            throw new InvalidOperationException("所选套图不在同一个人物目录或存储位置，不能直接合并。");
        parent = parents[0];
        var parentInfo = Directory.GetParent(parent);
        var standardRoot = parentInfo?.Parent?.FullName;
        var isStandardModelDirectory = PathsEqual(standardRoot, state.Settings.DownloadRoot) ||
                                       PathsEqual(standardRoot, state.Settings.ArchiveRoot);
        var isLegacyModelDirectory = state.Settings.LegacyDownloadRoots.Any(root =>
            PathsEqual(parentInfo?.FullName, root));
        if (!isStandardModelDirectory && !isLegacyModelDirectory)
            throw new InvalidOperationException("只能合并人物目录下第一层的套图，不能再次选择合集内部的分卷。");
        var targetPath = Path.Combine(parent, requestedTitle.Trim());
        if (Directory.Exists(targetPath) || File.Exists(targetPath))
            throw new IOException($"合并目标已经存在：{targetPath}");
        if (sources.Any(item => Path.GetFullPath(item.LocalDir).Equals(targetPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("合并后的名称不能与原套图目录相同。");

        var sourceIds = sources.Select(item => item.SetId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = sources.Select(item => NormalizePath(item.LocalDir))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogConflict = state.Catalog.Snapshot().FirstOrDefault(item =>
            item.Category.Equals(first.Category, StringComparison.OrdinalIgnoreCase) &&
            item.Model.Equals(first.Model, StringComparison.OrdinalIgnoreCase) &&
            item.Title.Equals(requestedTitle.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !sourceIds.Contains(item.SetId) &&
            !sourcePaths.Contains(NormalizePath(item.LocalDir)));
        if (catalogConflict != null)
            throw new InvalidOperationException("资源账本中已有同名套图，请换一个合集名称。");
        var resourceConflict = state.Database.ResourceSnapshot().FirstOrDefault(resource =>
            resource.Category.Equals(first.Category, StringComparison.OrdinalIgnoreCase) &&
            resource.Model.Equals(first.Model, StringComparison.OrdinalIgnoreCase) &&
            resource.Title.Equals(requestedTitle.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !sourcePaths.Contains(NormalizePath(resource.LocalDir)) &&
            !sources.Any(source => !string.IsNullOrWhiteSpace(source.SourceUrl) &&
                                   source.SourceUrl.Equals(resource.DetailUrl, StringComparison.OrdinalIgnoreCase)));
        if (resourceConflict != null)
            throw new InvalidOperationException("下载资源中已有同名记录，请换一个合集名称，避免影响下载去重。");
        var targetStat = new LocalStat { Model = first.Model, Title = requestedTitle.Trim() };
        if (state.Favorites.HasTitleConflict(targetStat, sources))
            throw new InvalidOperationException("收藏记录中已有同名套图，请换一个合集名称。");

        var incomplete = sources.SelectMany(item => Directory.EnumerateFiles(
                item.LocalDir,
                "*",
                SearchOption.AllDirectories))
            .FirstOrDefault(path => path.EndsWith(".BaiduPCS-Go-downloading", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                                    path.EndsWith(".copying", StringComparison.OrdinalIgnoreCase));
        if (incomplete != null)
            throw new InvalidOperationException($"套图中仍有未完成文件，暂不能合并：{Path.GetFileName(incomplete)}");

        if (!LibraryPaths.IsInside(targetPath, state.Settings.DownloadRoot) &&
            !LibraryPaths.IsInside(targetPath, state.Settings.ArchiveRoot) &&
            !state.Settings.LegacyDownloadRoots.Any(root => LibraryPaths.IsInside(targetPath, root)))
            throw new InvalidOperationException("合并目标不在当前媒体库管理范围内。");
        target = targetPath;
    }

    private static LocalStat BuildMergedStat(
        AppState state,
        IReadOnlyList<LocalStat> sources,
        string target,
        IReadOnlyList<string> partDirectories)
    {
        var imageExts = state.Settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videoExts = state.Settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .ToArray();
        var now = DateTime.Now.ToString("s");
        var videos = files.Where(file => videoExts.Contains(file.Extension)).ToArray();
        var invalidVideos = Math.Max(
            videos.Count(file => !VideoValidator.QuickHeaderLooksValid(file.FullName)),
            VideoValidator.MarkedInvalidCount(target));
        var imageCount = files.Count(file =>
            imageExts.Contains(file.Extension) &&
            MediaFileValidator.QuickImageHeaderLooksValid(file.FullName));
        var videoCount = Math.Max(0, videos.Length - invalidVideos);
        var expectedImages = sources.Sum(item => Math.Max(item.ExpectedImageCount, item.ImageCount));
        var expectedVideos = sources.Sum(item => Math.Max(
            item.ExpectedVideoCount,
            item.VideoCount + item.InvalidVideoCount));
        var partial = imageCount < expectedImages || videoCount + invalidVideos < expectedVideos;
        var availability = invalidVideos > 0
            ? CatalogStatuses.Corrupt
            : partial
                ? CatalogStatuses.Partial
                : CatalogStatuses.Available;
        var merged = new LocalStat
        {
            Category = sources[0].Category,
            Model = sources[0].Model,
            Title = Path.GetFileName(target),
            LocalDir = target,
            StorageTier = LibraryPaths.StorageTier(state.Settings, target),
            ImageCount = imageCount,
            VideoCount = videoCount,
            InvalidVideoCount = invalidVideos,
            TotalBytes = files.Sum(file => file.Length),
            ExpectedImageCount = expectedImages,
            ExpectedVideoCount = expectedVideos,
            ExpectedTotalBytes = sources.Sum(item => Math.Max(item.ExpectedTotalBytes, item.TotalBytes)),
            LastScanned = now,
            Availability = availability,
            AvailabilityReason = invalidVideos > 0
                ? $"检测到 {invalidVideos} 个损坏视频"
                : partial
                    ? "部分分卷的实际媒体数量低于历史完整记录"
                    : "",
            LastVerified = now,
            LastComplete = availability == CatalogStatuses.Available ? now : ""
        };
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            merged.MergedParts.Add(new MergedPartInfo
            {
                SourceSetId = source.SetId,
                Title = source.Title,
                RelativeDirectory = Path.GetRelativePath(target, partDirectories[index]),
                SourceUrl = source.SourceUrl,
                PanUrl = source.PanUrl,
                PanPassword = source.PanPassword,
                ExtractPassword = source.ExtractPassword
            });
        }
        return merged;
    }

    private static void UpdateResourceLocations(
        AppState state,
        IReadOnlyList<LocalStat> sources,
        IReadOnlyList<string> partDirectories)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var oldPath = NormalizePath(source.LocalDir);
            foreach (var resource in state.Database.Resources.Where(resource =>
                         NormalizePath(resource.LocalDir).Equals(oldPath, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrWhiteSpace(source.SourceUrl) &&
                          resource.DetailUrl.Equals(source.SourceUrl, StringComparison.OrdinalIgnoreCase))))
                resource.LocalDir = partDirectories[index];
        }
    }

    private static bool RollBackMoves(string staging, IReadOnlyList<(string Original, string Staged)> moved)
    {
        foreach (var part in moved.Reverse())
        {
            try
            {
                if (Directory.Exists(part.Staged) && !Directory.Exists(part.Original))
                    Directory.Move(part.Staged, part.Original);
            }
            catch { }
        }
        try
        {
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any())
                Directory.Delete(staging);
        }
        catch { }
        return moved.All(part => Directory.Exists(part.Original) && !Directory.Exists(part.Staged)) &&
               (!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
    }

    private static void WriteJournal(SetMergeJournal journal)
    {
        lock (JournalGate)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = JournalPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, Settings.JsonOptions), new UTF8Encoding(false));
            File.Move(temp, JournalPath, true);
        }
    }

    private static SetMergeJournal? ReadJournal()
    {
        lock (JournalGate)
        {
            if (!File.Exists(JournalPath)) return null;
            try
            {
                return JsonSerializer.Deserialize<SetMergeJournal>(
                    File.ReadAllText(JournalPath, Encoding.UTF8),
                    Settings.JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    private static void DeleteJournal()
    {
        lock (JournalGate)
        {
            try { if (File.Exists(JournalPath)) File.Delete(JournalPath); }
            catch { }
        }
    }

    private static int PartOrder(LocalStat item)
    {
        var title = item.Title;
        var marker = ExplicitPartMarker().Match(title);
        if (marker.Success)
        {
            var value = marker.Groups[1].Value;
            if (value is "上") return 100;
            if (value is "中") return 200;
            if (value is "下") return 300;
            if (int.TryParse(marker.Groups[2].Value, out var number)) return number * 100;
        }
        var suffix = BarePartSuffix().Match(title);
        return suffix.Success && int.TryParse(suffix.Groups[1].Value, out var part)
            ? part * 100
            : 10000;
    }

    private static string NaturalKey(string value) =>
        NumberToken().Replace(value, match => match.Value.PadLeft(12, '0'));

    private static string SafeSegment(string value)
    {
        var safe = new string(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray())
            .Trim(' ', '.');
        if (safe.Length > 120) safe = safe[..120].TrimEnd();
        return string.IsNullOrWhiteSpace(safe) ? "分卷" : safe;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"(?:^|[\s\-_（(【\[])(上|中|下)(?:集|部|卷)?(?:[\s\-_）)】\]]|$)|(?:part|第)\s*(\d+)(?:集|部|卷)?", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitPartMarker();

    [GeneratedRegex(@"[\s\-_（(【\[]+(\d{1,2})[\s）)】\]]*$")]
    private static partial Regex BarePartSuffix();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberToken();
}
