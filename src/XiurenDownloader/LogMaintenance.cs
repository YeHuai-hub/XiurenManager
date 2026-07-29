using System.Globalization;

namespace XiurenDownloader;

internal sealed record LogCleanupResult(int Files, long Bytes, int FailedFiles);

internal static class LogMaintenance
{
    public const int DefaultMaxFileMB = 20;
    private static readonly object Gate = new();

    public static string CurrentLogPath(string channel, int maxFileMB)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        var stem = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + channel;
        var limit = Math.Clamp(maxFileMB, 1, 1024) * 1024L * 1024L;

        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? "" : "." + index.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(AppPaths.LogDir, stem + suffix + ".log");
            if (!File.Exists(path) || new FileInfo(path).Length < limit)
                return path;
        }

        return Path.Combine(AppPaths.LogDir, stem + ".999.log");
    }

    public static LogCleanupResult Cleanup(Settings settings)
    {
        lock (Gate)
        {
            var result = new MutableResult();
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var files = GetLogFiles();
                var cutoff = DateTime.Today.AddDays(-Math.Clamp(settings.LogRetentionDays, 1, 3650) + 1);

                foreach (var file in files.Where(file => GetLogDate(file) < cutoff))
                    TryDelete(file, result);

                files = GetLogFiles();
                var total = files.Sum(file => file.Length);
                var maxTotal = Math.Clamp(settings.LogMaxTotalMB, 10, 10240) * 1024L * 1024L;
                if (total <= maxTotal)
                    return result.ToResult();

                var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFullPath(CurrentLogPath("", settings.LogMaxFileMB)),
                    Path.GetFullPath(CurrentLogPath("-wpf", settings.LogMaxFileMB))
                };

                foreach (var file in files
                             .Where(file => !protectedPaths.Contains(file.FullName))
                             .OrderBy(GetLogDate)
                             .ThenBy(file => file.LastWriteTimeUtc))
                {
                    if (total <= maxTotal) break;
                    var length = file.Length;
                    if (TryDelete(file, result))
                        total -= length;
                }
            }
            catch
            {
                result.FailedFiles++;
            }

            return result.ToResult();
        }
    }

    public static LogCleanupResult ClearAll()
    {
        lock (Gate)
        {
            var result = new MutableResult();
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                foreach (var file in GetLogFiles())
                    TryDelete(file, result);
            }
            catch
            {
                result.FailedFiles++;
            }
            return result.ToResult();
        }
    }

    private static FileInfo[] GetLogFiles() =>
        new DirectoryInfo(AppPaths.LogDir)
            .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
            .ToArray();

    private static DateTime GetLogDate(FileInfo file)
    {
        var prefix = Path.GetFileNameWithoutExtension(file.Name);
        return prefix.Length >= 8 &&
               DateTime.TryParseExact(
                   prefix[..8],
                   "yyyyMMdd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date)
            ? date
            : file.LastWriteTime.Date;
    }

    private static bool TryDelete(FileInfo file, MutableResult result)
    {
        try
        {
            var length = file.Length;
            file.Delete();
            result.Files++;
            result.Bytes += length;
            return true;
        }
        catch
        {
            result.FailedFiles++;
            return false;
        }
    }

    private sealed class MutableResult
    {
        public int Files { get; set; }
        public long Bytes { get; set; }
        public int FailedFiles { get; set; }

        public LogCleanupResult ToResult() => new(Files, Bytes, FailedFiles);
    }
}
