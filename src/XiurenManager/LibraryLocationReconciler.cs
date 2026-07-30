using XiurenDownloader;

namespace XiurenManager;

internal static class LibraryLocationReconciler
{
    public static int Reconcile(AppState state, bool notify = true)
    {
        var changed = 0;
        foreach (var item in state.Database.LocalFiles)
        {
            var migrated = MigratedPath(
                state.Settings,
                item.Category,
                item.LocalDir);
            if (migrated == null)
                continue;
            item.LocalDir = migrated;
            changed++;
        }

        foreach (var item in state.Database.Resources)
        {
            var migrated = MigratedPath(
                state.Settings,
                item.Category,
                item.LocalDir);
            if (migrated == null)
                continue;
            item.LocalDir = migrated;
            changed++;
        }

        if (changed == 0)
            return 0;
        state.Database.Save();
        state.WriteLog($"已同步迁移后的资源路径: {changed} 条");
        if (notify)
            state.NotifyDataChanged();
        return changed;
    }

    private static string? MigratedPath(
        Settings settings,
        string category,
        string currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) ||
            Directory.Exists(currentPath))
        {
            return null;
        }

        foreach (var legacyRoot in settings.LegacyDownloadRoots)
        {
            if (!IsInside(currentPath, legacyRoot))
                continue;
            var candidate = Path.Combine(
                LibraryPaths.CategoryRoot(settings, category),
                Path.GetRelativePath(legacyRoot, currentPath));
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
