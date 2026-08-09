using System.Text;
using System.Text.Json;

namespace XiurenDownloader;

internal enum MetadataSidecarWriteResult
{
    Skipped,
    Unchanged,
    Written
}

internal static class SetMetadataSidecar
{
    public const string FileName = "套图资料.md";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static bool IsMetadataFile(string path) =>
        Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase);

    public static MetadataSidecarWriteResult Write(
        Database database,
        FavoriteStore favorites,
        LocalStat item)
    {
        if (string.IsNullOrWhiteSpace(item.LocalDir) ||
            !Directory.Exists(item.LocalDir) ||
            AppPaths.IsInsideTool(item.LocalDir) ||
            item.ImageCount + item.VideoCount + item.InvalidVideoCount <= 0)
        {
            return MetadataSidecarWriteResult.Skipped;
        }

        var resource = FindBestResource(database, item);
        var favorite = favorites.GetMetadata(item);
        var content = BuildDocument(
            item,
            resource,
            favorite.Score,
            favorite.Tags);
        var path = Path.Combine(item.LocalDir, FileName);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
            return MetadataSidecarWriteResult.Unchanged;

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, Utf8NoBom);
            File.Move(temp, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch { }
        }
        return MetadataSidecarWriteResult.Written;
    }

    public static string? ReadArchivePassword(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var line = File.ReadLines(path, Encoding.UTF8)
                .FirstOrDefault(value => value.StartsWith(
                    "archive_password:",
                    StringComparison.OrdinalIgnoreCase));
            if (line == null) return null;
            var raw = line[(line.IndexOf(':') + 1)..].Trim();
            return JsonSerializer.Deserialize<string>(raw, Settings.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ResourceItem? FindBestResource(Database database, LocalStat item)
    {
        var itemPath = NormalizePath(item.LocalDir);
        var resources = database.ResourceSnapshot();
        var pathMatches = resources.Where(resource =>
                !string.IsNullOrWhiteSpace(resource.LocalDir) &&
                NormalizePath(resource.LocalDir).Equals(
                    itemPath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var candidates = pathMatches.Length > 0
            ? pathMatches
            : resources.Where(resource =>
                    resource.Model.Equals(item.Model, StringComparison.OrdinalIgnoreCase) &&
                    resource.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        return candidates
            .OrderByDescending(resource => !string.IsNullOrWhiteSpace(resource.PanUrl))
            .ThenByDescending(resource =>
                resource.ExtractStatus.Equals("Extracted", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(resource =>
                resource.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) ||
                resource.DownloadStatus.Equals("Done", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(resource => ParseDate(resource.LastChecked))
            .FirstOrDefault();
    }

    private static string BuildDocument(
        LocalStat item,
        ResourceItem? resource,
        int score,
        IReadOnlyList<string> tags)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        Add(builder, "schema", "xiuren-set/v1");
        Add(builder, "post_id", resource?.PostId ?? "");
        Add(builder, "title", item.Title);
        Add(builder, "category", item.Category);
        Add(builder, "model", item.Model);
        Add(builder, "resource_type", resource?.ResourceType ?? InferResourceType(item));
        Add(builder, "source_url", resource?.DetailUrl ?? "");
        Add(builder, "baidu_url", resource?.PanUrl ?? "");
        Add(builder, "baidu_code", resource?.PanPassword ?? "");
        Add(builder, "archive_password", resource?.ExtractPassword ?? "");
        builder.AppendLine($"score: {Math.Max(0, score)}");
        builder.AppendLine("tags:");
        foreach (var tag in tags)
            builder.AppendLine("  - " + Quote(tag));
        builder.AppendLine($"images: {Math.Max(0, item.ImageCount)}");
        builder.AppendLine($"videos: {Math.Max(0, item.VideoCount)}");
        builder.AppendLine($"invalid_videos: {Math.Max(0, item.InvalidVideoCount)}");
        builder.AppendLine($"total_bytes: {Math.Max(0, item.TotalBytes)}");
        Add(builder, "source_checked_at", resource?.LastChecked ?? "");
        builder.AppendLine($"source_record_matched: {(resource != null ? "true" : "false")}");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine("# " + item.Title);
        builder.AppendLine();
        builder.AppendLine("此文件由写真资源管理器自动维护。数据库是主数据源，请通过工具修改标签和喜爱值。");
        builder.AppendLine();
        builder.AppendLine("## 基本资料");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 内容 |");
        builder.AppendLine("| --- | --- |");
        Row(builder, "分类", item.Category);
        Row(builder, "模特", item.Model);
        Row(builder, "资源类型", resource?.ResourceType ?? InferResourceType(item));
        Row(builder, "图片", item.ImageCount.ToString("N0"));
        Row(builder, "视频", item.VideoCount.ToString("N0"));
        Row(builder, "损坏视频", item.InvalidVideoCount.ToString("N0"));
        Row(builder, "总大小", FormatBytes(item.TotalBytes));
        Row(builder, "喜爱值", Math.Max(0, score).ToString());
        Row(builder, "标签", tags.Count == 0 ? "暂无" : string.Join("、", tags));
        builder.AppendLine();
        builder.AppendLine("## 来源与密码");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 内容 |");
        builder.AppendLine("| --- | --- |");
        Row(builder, "网站详情页", Link(resource?.DetailUrl));
        Row(builder, "百度网盘", Link(resource?.PanUrl));
        Row(builder, "网盘提取码", resource?.PanPassword ?? "");
        Row(builder, "压缩包解压密码", resource?.ExtractPassword ?? "");
        if (resource == null)
        {
            builder.AppendLine();
            builder.AppendLine("> 当前本地套图没有可靠匹配的来源记录，因此网址和密码保持为空。");
        }
        return builder.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private static void Add(StringBuilder builder, string key, string value) =>
        builder.AppendLine($"{key}: {Quote(value)}");

    private static string Quote(string? value) =>
        JsonSerializer.Serialize(value ?? "", Settings.JsonOptions);

    private static void Row(StringBuilder builder, string name, string? value) =>
        builder.AppendLine($"| {EscapeCell(name)} | {EscapeCell(value)} |");

    private static string EscapeCell(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "未记录"
            : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Link(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"<{value.Trim()}>";

    private static string InferResourceType(LocalStat item) =>
        item.VideoCount > 0 && item.ImageCount == 0 ? "Video" : "Photo";

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, out var result) ? result : DateTime.MinValue;

    private static string NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetFullPath(value).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
