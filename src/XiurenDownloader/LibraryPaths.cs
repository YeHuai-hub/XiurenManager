namespace XiurenDownloader;

internal static class LibraryPaths
{
    public const string DefaultCategory = "秀人";
    public const string CosCategory = "COS";
    public const string WeemeCategory = "微密圈";

    public static string NormalizeCategory(string? category)
    {
        return string.IsNullOrWhiteSpace(category)
            ? DefaultCategory
            : XiurenClient.Safe(category.Trim());
    }

    public static string CategoryRoot(Settings settings, string? category = null)
    {
        return Path.Combine(
            settings.DownloadRoot,
            NormalizeCategory(category ?? settings.DownloadCategory));
    }

    public static string ModelRoot(Settings settings, string? category, string model)
    {
        return Path.Combine(
            CategoryRoot(settings, category),
            XiurenClient.Safe(model));
    }

    public static string SetRoot(
        Settings settings,
        string? category,
        string model,
        string title)
    {
        return Path.Combine(
            ModelRoot(settings, category, model),
            XiurenClient.Safe(title));
    }

    public static string ArchiveCategoryRoot(Settings settings, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(settings.ArchiveRoot)) return "";
        return Path.Combine(
            settings.ArchiveRoot,
            NormalizeCategory(category ?? settings.DownloadCategory));
    }

    public static string ArchiveModelRoot(Settings settings, string? category, string model)
    {
        var categoryRoot = ArchiveCategoryRoot(settings, category);
        return string.IsNullOrWhiteSpace(categoryRoot)
            ? ""
            : Path.Combine(categoryRoot, XiurenClient.Safe(model));
    }

    public static string DownloadModelRoot(
        Settings settings,
        Database database,
        string? category,
        string model)
    {
        var normalizedCategory = NormalizeCategory(category);
        var safeModel = XiurenClient.Safe(model);
        var local = ModelRoot(settings, normalizedCategory, safeModel);
        var archive = ArchiveModelRoot(settings, normalizedCategory, safeModel);
        if (string.IsNullOrWhiteSpace(archive)) return local;

        var trackedOnArchive = Directory.Exists(archive) ||
                               database.LocalFiles.Any(x =>
                                   x.Category.Equals(normalizedCategory, StringComparison.OrdinalIgnoreCase) &&
                                   x.Model.Equals(safeModel, StringComparison.OrdinalIgnoreCase) &&
                                   (x.StorageTier.Equals(StorageTiers.Archive, StringComparison.OrdinalIgnoreCase) ||
                                    IsInside(x.LocalDir, settings.ArchiveRoot))) ||
                               database.Resources.Any(x =>
                                   x.Category.Equals(normalizedCategory, StringComparison.OrdinalIgnoreCase) &&
                                   x.Model.Equals(safeModel, StringComparison.OrdinalIgnoreCase) &&
                                   IsInside(x.LocalDir, settings.ArchiveRoot));
        if (!trackedOnArchive) return local;
        if (!Directory.Exists(settings.ArchiveRoot))
        {
            throw new IOException(
                $"模特“{safeModel}”已存放在 NAS，但 NAS 当前不可用。为避免拆分资源，本次不会回退到本地下载。");
        }
        return archive;
    }

    public static string StorageTier(Settings settings, string path)
    {
        return IsInside(path, settings.ArchiveRoot)
            ? StorageTiers.Archive
            : StorageTiers.Local;
    }

    public static bool IsInside(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd('\\') + "\\";
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Categories(Settings settings)
    {
        return [DefaultCategory, CosCategory, WeemeCategory];
    }
}
