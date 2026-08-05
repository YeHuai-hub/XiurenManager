using System.Net;
using System.Text.RegularExpressions;

namespace XiurenDownloader;

internal sealed record SiteCategoryDetection(
    string Category,
    bool IsDetected,
    bool HasConflict,
    IReadOnlyList<string> Signals);

internal static class SiteCategoryClassifier
{
    public const string WebsiteSource = "Website";
    public const string DefaultSource = "Default";
    public const string LegacyFallbackSource = "Fallback";
    public const string CosCategory = LibraryPaths.CosCategory;
    public const string WeemeCategory = LibraryPaths.WeemeCategory;

    private static readonly Regex CategoryLink = new(
        "<a\\b(?=[^>]*\\brel=[\\\"'](?:[^\\\"']*\\s)?category(?:\\s[^\\\"']*)?[\\\"'])[^>]*\\bhref=[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WeemeTitle = new(
        "微\\s*[-·]?\\s*(?:密|秘)(?:圈)?|觅圈|秘语空间",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CosTitle = new(
        "(?:^|[^a-z])cos(?:play)?(?:[^a-z]|$)|角色扮演",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SiteCategoryDetection Detect(string html)
    {
        var signals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CategoryLink.Matches(html ?? ""))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var path = PathFromHref(href);
            if (MatchesRoot(path, "/weeme"))
                signals.Add(WeemeCategory);
            else if (MatchesRoot(path, "/cosplay-2"))
                signals.Add(CosCategory);
        }

        var ordered = signals.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return ordered.Length == 1
            ? new SiteCategoryDetection(ordered[0], true, false, ordered)
            : new SiteCategoryDetection(
                LibraryPaths.DefaultCategory,
                false,
                ordered.Length > 1,
                ordered);
    }

    public static string ResolveModelCategory(IEnumerable<ResourceItem> items)
    {
        var resources = items
            .Where(x => !string.IsNullOrWhiteSpace(x.DetailUrl) ||
                        !string.IsNullOrWhiteSpace(x.Title))
            .DistinctBy(EvidenceKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (resources.Count == 0)
            return LibraryPaths.DefaultCategory;

        var websiteCounts = CountCategories(resources, DetectedSpecialCategory);
        var websiteWinner = UniqueWinner(websiteCounts);
        if (!string.IsNullOrWhiteSpace(websiteWinner))
            return websiteWinner;

        // A site mirror can publish a few sets under another section. If the
        // website evidence ties, use explicit title evidence to break the tie.
        if (websiteCounts.Values.Sum() > 0)
        {
            var tied = websiteCounts
                .Where(x => x.Value == websiteCounts.Values.Max())
                .Select(x => x.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var titleCounts = CountCategories(resources, TitleSpecialCategory);
            var titleWinner = UniqueWinner(titleCounts
                .Where(x => tied.Contains(x.Key))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(titleWinner))
                return titleWinner;

            var currentWinner = CurrentSpecialCategory(resources, tied);
            return string.IsNullOrWhiteSpace(currentWinner)
                ? LibraryPaths.DefaultCategory
                : currentWinner;
        }

        // Older records do not have persisted website evidence. Titles are
        // only allowed to reclassify a model when they represent at least half
        // of that model's distinct sets, so a few crossover sets do not move a
        // large existing library.
        var fallbackTitleCounts = CountCategories(resources, TitleSpecialCategory);
        var fallbackTitleWinner = UniqueWinner(fallbackTitleCounts);
        if (!string.IsNullOrWhiteSpace(fallbackTitleWinner) &&
            fallbackTitleCounts[fallbackTitleWinner] >= Math.Max(1, (resources.Count + 1) / 2))
        {
            return fallbackTitleWinner;
        }

        return CurrentSpecialCategory(resources) is { Length: > 0 } current
            ? current
            : LibraryPaths.DefaultCategory;
    }

    internal static string TitleSpecialCategory(ResourceItem item)
    {
        var title = WebUtility.HtmlDecode(item.Title ?? "");
        if (WeemeTitle.IsMatch(title))
            return WeemeCategory;
        if (CosTitle.IsMatch(title))
            return CosCategory;
        return "";
    }

    public static string DetectedSpecialCategory(ResourceItem item)
    {
        if (IsSpecialCategory(item.DetectedCategory))
            return LibraryPaths.NormalizeCategory(item.DetectedCategory);

        // Compatibility with records created before DetectedCategory was persisted.
        if (item.CategorySource.Equals(WebsiteSource, StringComparison.OrdinalIgnoreCase) &&
            IsSpecialCategory(item.Category))
        {
            return LibraryPaths.NormalizeCategory(item.Category);
        }

        return "";
    }

    public static bool IsSpecialCategory(string? category)
    {
        return category?.Equals(CosCategory, StringComparison.OrdinalIgnoreCase) == true ||
               category?.Equals(WeemeCategory, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static Dictionary<string, int> CountCategories(
        IEnumerable<ResourceItem> items,
        Func<ResourceItem, string> selector)
    {
        return items
            .Select(selector)
            .Where(IsSpecialCategory)
            .GroupBy(LibraryPaths.NormalizeCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static string UniqueWinner(IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return "";
        var maximum = counts.Values.Max();
        var winners = counts.Where(x => x.Value == maximum).Select(x => x.Key).ToArray();
        return winners.Length == 1 ? winners[0] : "";
    }

    private static string CurrentSpecialCategory(
        IEnumerable<ResourceItem> items,
        ISet<string>? allowed = null)
    {
        var counts = items
            .Select(x => LibraryPaths.NormalizeCategory(x.Category))
            .Where(x => IsSpecialCategory(x) && (allowed == null || allowed.Contains(x)))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        return UniqueWinner(counts);
    }

    private static string EvidenceKey(ResourceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.DetailUrl))
            return item.DetailUrl.Trim();
        return Regex.Replace(WebUtility.HtmlDecode(item.Title ?? ""), "\\s+", " ").Trim();
    }

    private static string PathFromHref(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.AbsolutePath.TrimEnd('/');

        var value = href.Split('?', '#')[0];
        if (!value.StartsWith('/')) value = "/" + value;
        return value.TrimEnd('/');
    }

    private static bool MatchesRoot(string path, string root)
    {
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }
}
