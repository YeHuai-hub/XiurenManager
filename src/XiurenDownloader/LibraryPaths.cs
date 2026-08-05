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

    public static IReadOnlyList<string> Categories(Settings settings)
    {
        return [DefaultCategory, CosCategory, WeemeCategory];
    }
}
