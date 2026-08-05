using XiurenDownloader;

namespace XiurenManager;

internal static class ModelCategoryUnifier
{
    public static string ReconcileModel(
        AppState state,
        string model,
        IEnumerable<ResourceItem> currentItems)
    {
        var items = state.Database.Resources
            .Where(x => x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Concat(currentItems)
            .DistinctBy(x => x.DetailUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetCategory = SiteCategoryClassifier.ResolveModelCategory(items);
        var sourceCategories = items
            .Select(x => LibraryPaths.NormalizeCategory(x.Category))
            .Where(x => !x.Equals(targetCategory, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var sourceCategory in sourceCategories)
            MergeModelRoot(state.Settings, sourceCategory, targetCategory, model);

        foreach (var item in items)
        {
            var oldCategory = item.Category;
            var detected = SiteCategoryClassifier.DetectedSpecialCategory(item);
            if (!string.IsNullOrWhiteSpace(detected))
                item.DetectedCategory = detected;
            item.Category = targetCategory;
            item.LocalDir = RemapPath(
                state.Settings,
                oldCategory,
                targetCategory,
                model,
                item.LocalDir);
        }

        foreach (var local in state.Database.LocalFiles.Where(x =>
                     x.Model.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            var oldCategory = local.Category;
            local.Category = targetCategory;
            local.LocalDir = RemapPath(
                state.Settings,
                oldCategory,
                targetCategory,
                model,
                local.LocalDir);
        }

        return targetCategory;
    }

    public static int ReconcileSplitModels(AppState state, bool notify = true)
    {
        var groups = state.Database.Resources
            .Where(x => !string.IsNullOrWhiteSpace(x.Model))
            .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .Where(g =>
            {
                var target = SiteCategoryClassifier.ResolveModelCategory(g);
                return g.Any(x => !LibraryPaths.NormalizeCategory(x.Category)
                           .Equals(target, StringComparison.OrdinalIgnoreCase)) ||
                       state.Database.LocalFiles.Any(x =>
                           x.Model.Equals(g.Key, StringComparison.OrdinalIgnoreCase) &&
                           !LibraryPaths.NormalizeCategory(x.Category)
                               .Equals(target, StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
        var changed = 0;

        foreach (var group in groups)
        {
            var items = group.ToList();
            var targetCategory = SiteCategoryClassifier.ResolveModelCategory(items);
            var sourceCategories = items
                .Select(x => LibraryPaths.NormalizeCategory(x.Category))
                .Where(x => !x.Equals(targetCategory, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            try
            {
                foreach (var sourceCategory in sourceCategories)
                    MergeModelRoot(state.Settings, sourceCategory, targetCategory, group.Key);

                foreach (var item in items)
                {
                    var oldCategory = item.Category;
                    var detected = SiteCategoryClassifier.DetectedSpecialCategory(item);
                    if (!string.IsNullOrWhiteSpace(detected))
                        item.DetectedCategory = detected;
                    item.Category = targetCategory;
                    item.LocalDir = RemapPath(
                        state.Settings,
                        oldCategory,
                        targetCategory,
                        group.Key,
                        item.LocalDir);
                }

                foreach (var local in state.Database.LocalFiles.Where(x =>
                             x.Model.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var oldCategory = local.Category;
                    local.Category = targetCategory;
                    local.LocalDir = RemapPath(
                        state.Settings,
                        oldCategory,
                        targetCategory,
                        group.Key,
                        local.LocalDir);
                }

                changed++;
                state.WriteLog($"模特分类已纠正: {group.Key} → {targetCategory}");
            }
            catch (Exception ex)
            {
                state.WriteLog($"模特跨分类合并失败，文件均已保留: {group.Key} | {ex.Message}");
            }
        }

        if (changed == 0)
            return 0;
        state.Database.Save();
        if (notify)
            state.NotifyDataChanged();
        return changed;
    }

    private static void MergeModelRoot(
        Settings settings,
        string sourceCategory,
        string targetCategory,
        string model)
    {
        var source = LibraryPaths.ModelRoot(settings, sourceCategory, model);
        if (!Directory.Exists(source))
            return;

        var target = LibraryPaths.ModelRoot(settings, targetCategory, model);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!Directory.Exists(target))
        {
            Directory.Move(source, target);
            return;
        }

        MergeDirectory(source, target, sourceCategory);
        Directory.Delete(source, true);
    }

    private static void MergeDirectory(string source, string target, string sourceCategory)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            MergeDirectory(directory, destination, sourceCategory);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination))
                destination = UniqueCollisionPath(destination, sourceCategory);
            File.Move(file, destination);
        }
    }

    private static string UniqueCollisionPath(string path, string sourceCategory)
    {
        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var suffix = $"__来自{XiurenClient.Safe(sourceCategory)}";
        var candidate = Path.Combine(directory, stem + suffix + extension);
        for (var index = 2; File.Exists(candidate); index++)
            candidate = Path.Combine(directory, stem + suffix + "_" + index + extension);
        return candidate;
    }

    private static string RemapPath(
        Settings settings,
        string oldCategory,
        string targetCategory,
        string model,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            var oldRoot = Path.GetFullPath(
                LibraryPaths.ModelRoot(settings, oldCategory, model)).TrimEnd('\\');
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.Equals(oldRoot, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(oldRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return Path.Combine(
                LibraryPaths.ModelRoot(settings, targetCategory, model),
                Path.GetRelativePath(oldRoot, fullPath));
        }
        catch
        {
            return path;
        }
    }
}
