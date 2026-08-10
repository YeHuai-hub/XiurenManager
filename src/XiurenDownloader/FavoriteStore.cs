using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiurenDownloader;

internal sealed class FavoriteEntry
{
    public string SetId { get; set; } = "";
    public string LocalDir { get; set; } = "";
    public string Model { get; set; } = "";
    public string Title { get; set; } = "";
    public int Score { get; set; }
    public List<string> Tags { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
    public string UpdatedAt { get; set; } = "";
}

internal sealed class FavoriteStore
{
    private static readonly object FileGate = new();
    private readonly List<FavoriteEntry> entries;

    private FavoriteStore(List<FavoriteEntry> entries)
    {
        this.entries = entries;
    }

    public static FavoriteStore Load()
    {
        AppPaths.Ensure();
        lock (FileGate)
        {
            if (!File.Exists(AppPaths.FavoritesFile))
                return new FavoriteStore([]);

            try
            {
                var values = JsonSerializer.Deserialize<List<FavoriteEntry>>(
                    File.ReadAllText(AppPaths.FavoritesFile, Encoding.UTF8),
                    Settings.JsonOptions);
                var store = new FavoriteStore(values ?? []);
                if (store.MigrateNotes())
                {
                    try { store.Save(); } catch { }
                }
                return store;
            }
            catch
            {
                return new FavoriteStore([]);
            }
        }
    }

    public int GetScore(LocalStat item)
    {
        lock (FileGate)
            return Find(item)?.Score ?? 0;
    }

    public IReadOnlyList<string> GetTags(LocalStat item)
    {
        lock (FileGate)
            return Find(item)?.Tags?.ToArray() ?? [];
    }

    public (int Score, IReadOnlyList<string> Tags) GetMetadata(LocalStat item)
    {
        lock (FileGate)
        {
            var entry = Find(item);
            return (entry?.Score ?? 0, entry?.Tags?.ToArray() ?? []);
        }
    }

    public (int Score, DateTime UpdatedAt) GetModelPriority(string model)
    {
        lock (FileGate)
        {
            var matches = entries.Where(x =>
                x.Model.Equals(model, StringComparison.OrdinalIgnoreCase)).ToArray();
            var updatedAt = matches
                .Select(x => DateTime.TryParse(x.UpdatedAt, out var value) ? value : DateTime.MinValue)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return (matches.Sum(x => x.Score), updatedAt);
        }
    }

    public int ChangeScore(LocalStat item, int delta)
    {
        lock (FileGate)
        {
            var entry = Find(item);
            if (entry == null)
            {
                if (delta <= 0) return 0;
                entry = new FavoriteEntry();
                entries.Add(entry);
            }

            entry.LocalDir = NormalizePath(item.LocalDir);
            entry.SetId = item.SetId;
            entry.Model = item.Model;
            entry.Title = item.Title;
            entry.Score = Math.Max(0, entry.Score + delta);
            entry.UpdatedAt = DateTime.Now.ToString("s");
            var result = entry.Score;
            if (entry.Score == 0 && entry.Tags.Count == 0)
                entries.Remove(entry);
            Save();
            return result;
        }
    }

    public IReadOnlyList<string> SetTags(LocalStat item, IEnumerable<string> tags)
    {
        lock (FileGate)
        {
            var values = NormalizeTags(tags);
            var entry = Find(item);
            if (entry == null)
            {
                if (values.Count == 0) return [];
                entry = new FavoriteEntry();
                entries.Add(entry);
            }

            entry.LocalDir = NormalizePath(item.LocalDir);
            entry.SetId = item.SetId;
            entry.Model = item.Model;
            entry.Title = item.Title;
            entry.Tags = values;
            entry.Note = null;
            entry.UpdatedAt = DateTime.Now.ToString("s");
            if (entry.Score == 0 && entry.Tags.Count == 0)
                entries.Remove(entry);
            Save();
            return values.ToArray();
        }
    }

    public void UpdateLocation(LocalStat item, string localDir, string model, string title)
    {
        lock (FileGate)
        {
            var entry = Find(item);
            if (entry == null) return;

            entry.LocalDir = NormalizePath(localDir);
            entry.SetId = item.SetId;
            entry.Model = model;
            entry.Title = title;
            entry.UpdatedAt = DateTime.Now.ToString("s");
            Save();
        }
    }

    public void UpdateModelLocations(
        string model,
        string sourceRoot,
        string destinationRoot)
    {
        lock (FileGate)
        {
            var changed = false;
            foreach (var entry in entries.Where(x =>
                         x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
                         IsInside(x.LocalDir, sourceRoot)))
            {
                entry.LocalDir = NormalizePath(Path.Combine(
                    destinationRoot,
                    Path.GetRelativePath(sourceRoot, entry.LocalDir)));
                entry.UpdatedAt = DateTime.Now.ToString("s");
                changed = true;
            }
            if (changed) Save();
        }
    }

    public void ReconcileSetIds(IEnumerable<LocalStat> catalog)
    {
        lock (FileGate)
        {
            var items = catalog.ToArray();
            var changed = false;
            foreach (var entry in entries.Where(x => string.IsNullOrWhiteSpace(x.SetId)))
            {
                var path = NormalizePath(entry.LocalDir);
                var match = items.FirstOrDefault(item =>
                    NormalizePath(item.LocalDir).Equals(
                        path,
                        StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var titleMatches = items.Where(item =>
                            item.Model.Equals(entry.Model, StringComparison.OrdinalIgnoreCase) &&
                            item.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase))
                        .Take(2)
                        .ToArray();
                    if (titleMatches.Length == 1)
                        match = titleMatches[0];
                }
                if (match == null || string.IsNullOrWhiteSpace(match.SetId)) continue;
                entry.SetId = match.SetId;
                changed = true;
            }
            if (changed) Save();
        }
    }

    private FavoriteEntry? Find(LocalStat item)
    {
        if (!string.IsNullOrWhiteSpace(item.SetId))
        {
            var byId = entries.FirstOrDefault(x =>
                x.SetId.Equals(item.SetId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        var dir = NormalizePath(item.LocalDir);
        var byPath = entries.FirstOrDefault(x =>
            NormalizePath(x.LocalDir).Equals(dir, StringComparison.OrdinalIgnoreCase));
        if (byPath != null) return byPath;

        return entries.FirstOrDefault(x =>
            x.Model.Equals(item.Model, StringComparison.OrdinalIgnoreCase) &&
            x.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase));
    }

    private bool MigrateNotes()
    {
        var changed = false;
        foreach (var entry in entries)
        {
            entry.Tags ??= [];
            if (!string.IsNullOrWhiteSpace(entry.Note))
            {
                var value = entry.Note.Trim();
                if (!entry.Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
                    entry.Tags.Add(value);
                entry.Note = null;
                changed = true;
            }
        }
        return changed;
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        return tags
            .Select(x => x?.Trim() ?? "")
            .Where(x => x.Length > 0)
            .Where(x => x.Length <= 30)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();
    }

    private void Save()
    {
        lock (FileGate)
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var json = JsonSerializer.Serialize(
                entries.OrderByDescending(x => x.Score).ThenBy(x => x.Model).ThenBy(x => x.Title),
                Settings.JsonOptions);
            var temp = AppPaths.FavoritesFile + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, AppPaths.FavoritesFile, true);
        }
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

    private static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var fullPath = NormalizePath(path) + "\\";
            var fullRoot = NormalizePath(root) + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
