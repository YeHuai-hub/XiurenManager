using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XiurenDownloader;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(x => x.Equals("--search-download", StringComparison.OrdinalIgnoreCase)))
            return Headless.SearchDownloadAsync(args).GetAwaiter().GetResult();

        if (args.Any(x => x.Equals("--download-ready", StringComparison.OrdinalIgnoreCase)))
            return Headless.DownloadReadyAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

internal static class Headless
{
    private static readonly object LogGate = new();

    public static async Task<int> SearchDownloadAsync(string[] args)
    {
        AppPaths.Ensure();
        try
        {
            var keyword = GetArgValue(args, "--search-download").Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                WriteLog("后台搜索失败: 缺少搜索内容。");
                return 2;
            }

            var pages = ParsePositiveInt(GetArgValue(args, "--pages"), 999);
            var maxReady = ParsePositiveInt(GetArgValue(args, "--max-ready"), 9999);
            var aliases = GetArgValue(args, "--aliases");
            var exclusions = XiurenClient.ExclusionTerms(GetArgValue(args, "--exclude"));
            var settings = Settings.Load();
            var db = Database.Load();
            var model = XiurenClient.Safe(keyword);
            var progress = new Progress<string>(WriteLog);
            var merged = new Dictionary<string, ResourceItem>(StringComparer.OrdinalIgnoreCase);

            WriteLog("后台搜索并下载: " + keyword);
            foreach (var searchName in XiurenClient.SearchNames(keyword, aliases))
            {
                if (maxReady > 0 && merged.Count >= maxReady) break;
                var remaining = maxReady > 0 ? maxReady - merged.Count : 0;
                WriteLog($"搜索名称: {searchName} → 统一归档: {model}");
                var found = await new XiurenClient(settings).SearchAsync(
                    searchName,
                    pages,
                    remaining,
                    progress,
                    CancellationToken.None,
                    detailUrl =>
                    {
                        var saved = db.Resources.FirstOrDefault(x =>
                            x.DetailUrl.Equals(detailUrl, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.PanUrl));
                        if (saved != null) saved.Model = model;
                        return saved;
                    },
                    item =>
                    {
                        item.Model = model;
                        var stored = db.Upsert(item);
                        stored.Model = model;
                        db.Save();
                    },
                    exclusions);

                foreach (var item in found)
                {
                    item.Model = model;
                    var stored = db.Upsert(item);
                    stored.Model = model;
                    merged[stored.DetailUrl] = stored;
                }
                db.Save();
            }

            var items = merged.Values.ToList();
            WriteLog("本轮已入库并准备下载: " + items.Count + " 条");
            await new Downloader(settings, db, progress).RunAsync(items, CancellationToken.None);
            db.Save();
            WriteLog("后台搜索下载任务结束。");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog("后台搜索下载失败: " + ErrorText.Format(ex).Replace(Environment.NewLine, " | "));
            return 1;
        }
    }

    public static async Task<int> DownloadReadyAsync(string[] args)
    {
        AppPaths.Ensure();
        try
        {
            var settings = Settings.Load();
            var db = Database.Load();
            var model = GetArgValue(args, "--model");
            var changed = RepairMissingCompletedResources(settings, db, model);
            if (changed > 0) WriteLog("已把本地缺失/损坏的已完成记录改回待下载: " + changed + " 条");

            var items = db.Resources
                .Where(x => x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(x.PanUrl))
                .Where(x => !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(model) || x.Model.Equals(XiurenClient.Safe(model), StringComparison.OrdinalIgnoreCase))
                .ToList();

            WriteLog(string.IsNullOrWhiteSpace(model)
                ? "离线下载已入库链接: " + items.Count + " 条"
                : "离线下载已入库链接: " + XiurenClient.Safe(model) + "，" + items.Count + " 条");

            await new Downloader(settings, db, new Progress<string>(WriteLog)).RunAsync(items, CancellationToken.None);
            db.Save();
            WriteLog("离线下载任务结束。");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog("离线下载失败: " + ErrorText.Format(ex).Replace(Environment.NewLine, " | "));
            return 1;
        }
    }

    private static string GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return "";
    }

    private static int ParsePositiveInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int RepairMissingCompletedResources(Settings settings, Database db, string modelFilter)
    {
        var model = XiurenClient.Safe((modelFilter ?? "").Trim());
        var changed = 0;
        foreach (var r in db.Resources.Where(x =>
                     x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) &&
                     (string.IsNullOrWhiteSpace(model) || x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))))
        {
            if (HasUsableLocalMedia(settings, r)) continue;
            r.Status = "Ready";
            r.DownloadStatus = "";
            r.ExtractStatus = "";
            r.Error = "本地文件缺失或视频损坏，等待重新下载";
            changed++;
        }
        if (changed > 0) db.Save();
        return changed;
    }

    private static bool HasUsableLocalMedia(Settings settings, ResourceItem r)
    {
        foreach (var dir in CandidateLocalDirs(settings, r))
        {
            if (!Directory.Exists(dir)) continue;
            if (VideoValidator.HasInvalidMarker(dir)) continue;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(x => !AppPaths.IsInsideTool(x));
            if (files.Any(f => IsImage(settings, f) || IsValidVideo(settings, f))) return true;
        }
        if (!string.IsNullOrWhiteSpace(r.Model))
        {
            var modelDir = LibraryPaths.ModelRoot(settings, r.Category, r.Model);
            if (Directory.Exists(modelDir) && Directory.GetFiles(modelDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(f => Downloader.LooseMediaMatchesTitle(f, r.Title) &&
                              (IsImage(settings, f) || IsValidVideo(settings, f))))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> CandidateLocalDirs(Settings settings, ResourceItem r)
    {
        if (!string.IsNullOrWhiteSpace(r.LocalDir)) yield return r.LocalDir;
        if (!string.IsNullOrWhiteSpace(r.Model) && !string.IsNullOrWhiteSpace(r.Title))
            yield return LibraryPaths.SetRoot(settings, r.Category, r.Model, r.Title);
    }

    private static bool IsImage(Settings settings, string f) =>
        MediaFileValidator.QuickImageHeaderLooksValid(f) &&
        settings.ImageExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    private static bool IsValidVideo(Settings settings, string f)
    {
        if (!settings.VideoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext is not ".mp4" and not ".m4v" and not ".mov") return true;
        try
        {
            using var stream = File.OpenRead(f);
            var length = (int)Math.Min(stream.Length, 4096);
            if (length < 16) return false;
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            var header = Encoding.ASCII.GetString(buffer, 0, read);
            return header.Contains("ftyp", StringComparison.Ordinal) ||
                   header.Contains("moov", StringComparison.Ordinal) ||
                   header.Contains("mdat", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteLog(string text)
    {
        var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
        lock (LogGate)
        {
            File.AppendAllText(
                LogMaintenance.CurrentLogPath("", LogMaintenance.DefaultMaxFileMB),
                line + Environment.NewLine,
                Encoding.UTF8);
        }
    }
}

internal static class AppPaths
{
    public static readonly string ProgramRoot = AppContext.BaseDirectory.TrimEnd('\\');
    public static readonly string ToolRoot = FindToolRoot();
    public static readonly string ConfigDir = Path.Combine(ToolRoot, "config");
    public static readonly string DataDir = Path.Combine(ToolRoot, "data");
    public static readonly string LogDir = Path.Combine(ToolRoot, "logs");
    public static readonly string SettingsFile = Path.Combine(ConfigDir, "settings.json");
    public static readonly string DbFile = Path.Combine(DataDir, "xiuren.db");
    public static readonly string FavoritesFile = Path.Combine(DataDir, "favorites.json");
    public static string DownloadRoot => Directory.GetParent(ToolRoot)?.FullName ?? ToolRoot;
    public static string LibraryRoot => Path.Combine(
        Path.GetPathRoot(ToolRoot) ?? DownloadRoot,
        "资源");

    public static void Ensure()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }

    public static bool IsInsideTool(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var root = Path.GetFullPath(ToolRoot).TrimEnd('\\');
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindToolRoot()
    {
        var environmentRoot = Environment.GetEnvironmentVariable("XIUREN_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(environmentRoot))
            return Environment.ExpandEnvironmentVariables(environmentRoot.Trim()).TrimEnd('\\');

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\XiurenManager");
            if (key?.GetValue("DataRoot") is string registryRoot &&
                !string.IsNullOrWhiteSpace(registryRoot))
            {
                return Environment.ExpandEnvironmentVariables(registryRoot.Trim()).TrimEnd('\\');
            }
        }
        catch { }

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (d.Name.Equals("_Tool", StringComparison.OrdinalIgnoreCase)) return d.FullName;
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
            if (d.Name.Equals("_Tool", StringComparison.OrdinalIgnoreCase)) return d.FullName;

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localData, "XiurenManager");
    }
}

internal static class ErrorText
{
    public static string Format(Exception ex)
    {
        var messages = new List<string>();
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(cur.Message) && !messages.Contains(cur.Message))
                messages.Add(cur.Message);
        }
        return string.Join(Environment.NewLine, messages);
    }
}

internal sealed class VideoValidationResult
{
    public string Path { get; set; } = "";
    public bool IsValid { get; set; }
    public string Error { get; set; } = "";
    public double DurationSeconds { get; set; }
}

internal static class MediaFileValidator
{
    public static bool QuickImageHeaderLooksValid(string file)
    {
        try
        {
            Span<byte> header = stackalloc byte[32];
            using var stream = File.OpenRead(file);
            if (stream.Length < 16) return false;
            var read = stream.Read(header);
            if (read < 4) return false;

            return header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF ||
                   read >= 8 &&
                   header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                   header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A ||
                   header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8' ||
                   header[0] == (byte)'B' && header[1] == (byte)'M' ||
                   read >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
                   header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P' ||
                   header[0] == (byte)'I' && header[1] == (byte)'I' && header[2] == 0x2A && header[3] == 0x00 ||
                   header[0] == (byte)'M' && header[1] == (byte)'M' && header[2] == 0x00 && header[3] == 0x2A ||
                   read >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p';
        }
        catch
        {
            return false;
        }
    }

    public static bool IsUsable(
        string file,
        IEnumerable<string> imageExtensions,
        IEnumerable<string> videoExtensions)
    {
        var extension = Path.GetExtension(file);
        if (imageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return QuickImageHeaderLooksValid(file);
        if (videoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return VideoValidator.QuickHeaderLooksValid(file);
        return false;
    }
}

internal static class VideoValidator
{
    public const string InvalidMarkerName = ".xiuren-invalid-media.json";

    public static VideoValidationResult Check(string file, Settings settings, bool sampleFrames, CancellationToken ct = default)
    {
        if (!File.Exists(file))
            return Invalid(file, "文件不存在");
        if (new FileInfo(file).Length < 16)
            return Invalid(file, "文件为空或过小");
        if (string.IsNullOrWhiteSpace(settings.FfprobePath) || !File.Exists(settings.FfprobePath))
            return QuickHeaderLooksValid(file) ? Valid(file, 0) : Invalid(file, "文件头无效，且未找到 ffprobe");

        var probe = RunTool(settings.FfprobePath,
        [
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=codec_type,width,height:format=duration",
            "-of", "json",
            file
        ], ct, 30_000);
        if (probe.ExitCode != 0)
            return Invalid(file, FirstUsefulLine(probe.StdErr, "ffprobe 无法读取视频"));
        if (HasSeriousDecoderError(probe.StdErr))
            return Invalid(file, FirstUsefulLine(probe.StdErr, "视频数据存在错误"));

        double duration;
        try
        {
            using var json = JsonDocument.Parse(probe.StdOut);
            var root = json.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
                return Invalid(file, "没有视频轨");
            var stream = streams[0];
            if (!stream.TryGetProperty("width", out var width) || width.GetInt32() <= 0 ||
                !stream.TryGetProperty("height", out var height) || height.GetInt32() <= 0)
                return Invalid(file, "视频尺寸无效");
            duration = ReadDuration(root);
            if (duration <= 0)
                return Invalid(file, "视频时长无效");
        }
        catch (Exception ex)
        {
            return Invalid(file, "ffprobe 返回内容无法解析: " + ex.Message);
        }

        if (!sampleFrames) return Valid(file, duration);
        var ffmpeg = Path.Combine(Path.GetDirectoryName(settings.FfprobePath) ?? "", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            return Valid(file, duration);

        var points = new[] { 0d, duration / 2, Math.Max(0, duration - 1) }
            .Select(x => Math.Round(x, 3))
            .Distinct()
            .ToArray();
        foreach (var point in points)
        {
            var decode = RunTool(ffmpeg,
            [
                "-hide_banner", "-v", "error",
                "-ss", point.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", file,
                "-map", "0:v:0",
                "-frames:v", "1",
                "-f", "null",
                "NUL"
            ], ct, 30_000);
            if (decode.ExitCode != 0 || HasSeriousDecoderError(decode.StdErr))
                return Invalid(file, $"在 {point:0.###} 秒解码失败: " + FirstUsefulLine(decode.StdErr, "无法解码画面"));
        }
        return Valid(file, duration);
    }

    public static bool QuickHeaderLooksValid(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var length = (int)Math.Min(stream.Length, 4096);
            if (length < 16) return false;
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".mp4" or ".m4v" or ".mov")
            {
                var header = Encoding.ASCII.GetString(buffer, 0, read);
                return header.Contains("ftyp", StringComparison.Ordinal);
            }
            if (ext == ".avi")
                return read >= 12 && Encoding.ASCII.GetString(buffer, 0, 4) == "RIFF" &&
                       Encoding.ASCII.GetString(buffer, 8, 4) == "AVI ";
            if (ext is ".mkv" or ".webm")
                return read >= 4 && buffer[0] == 0x1A && buffer[1] == 0x45 && buffer[2] == 0xDF && buffer[3] == 0xA3;
            if (ext == ".ts")
                return buffer[0] == 0x47;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasInvalidMarker(string dir) => File.Exists(Path.Combine(dir, InvalidMarkerName));

    public static int MarkedInvalidCount(string dir)
    {
        try
        {
            var path = Path.Combine(dir, InvalidMarkerName);
            if (!File.Exists(path)) return 0;
            using var json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return json.RootElement.TryGetProperty("Files", out var files) ? files.GetArrayLength() : 0;
        }
        catch
        {
            return 1;
        }
    }

    public static void WriteInvalidMarker(string dir, IEnumerable<VideoValidationResult> invalid)
    {
        Directory.CreateDirectory(dir);
        var files = invalid.Select(x => new
        {
            Path = Path.GetRelativePath(dir, x.Path),
            x.Error
        }).ToArray();
        var payload = new { CheckedAt = DateTime.Now.ToString("s"), Files = files };
        File.WriteAllText(
            Path.Combine(dir, InvalidMarkerName),
            JsonSerializer.Serialize(payload, Settings.JsonOptions),
            Encoding.UTF8);
    }

    public static void ClearInvalidMarker(string dir)
    {
        var path = Path.Combine(dir, InvalidMarkerName);
        if (File.Exists(path)) File.Delete(path);
    }

    public static int DeleteMarkedInvalidFiles(string dir)
    {
        var marker = Path.Combine(dir, InvalidMarkerName);
        if (!File.Exists(marker)) return 0;
        var removed = 0;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker, Encoding.UTF8));
            if (json.RootElement.TryGetProperty("Files", out var files))
            {
                foreach (var entry in files.EnumerateArray())
                {
                    if (!entry.TryGetProperty("Path", out var pathElement)) continue;
                    var relative = pathElement.GetString() ?? "";
                    var full = Path.GetFullPath(Path.Combine(dir, relative));
                    var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;
                    File.Delete(full);
                    removed++;
                }
            }
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
        return removed;
    }

    private static double ReadDuration(JsonElement root)
    {
        if (!root.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var value))
            return 0;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0;
    }

    private static bool HasSeriousDecoderError(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Any(x => !x.Contains("warning: first frame is no keyframe", StringComparison.OrdinalIgnoreCase));
    }

    private static string FirstUsefulLine(string text, string fallback) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? fallback;

    private static VideoValidationResult Valid(string file, double duration) =>
        new() { Path = file, IsValid = true, DurationSeconds = duration };

    private static VideoValidationResult Invalid(string file, string error) =>
        new() { Path = file, IsValid = false, Error = error };

    private static ToolResult RunTool(string exe, IEnumerable<string> args, CancellationToken ct, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var started = Stopwatch.StartNew();
        while (!process.WaitForExit(200))
        {
            if (ct.IsCancellationRequested || started.ElapsedMilliseconds > timeoutMs)
            {
                try { process.Kill(true); } catch { }
                ct.ThrowIfCancellationRequested();
                return new ToolResult(-1, "", "媒体检查超时");
            }
        }
        Task.WaitAll(stdout, stderr);
        return new ToolResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private sealed record ToolResult(int ExitCode, string StdOut, string StdErr);
}

internal sealed class Settings
{
    public string BaseUrl { get; set; } = "https://260704.xiurentua.cc";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string SearchMode { get; set; } = "Global";
    public string CategoryPath { get; set; } = "/tbgx";
    public string DownloadRoot { get; set; } = AppPaths.LibraryRoot;
    public string ArchiveRoot { get; set; } = @"\\YeHuai_NAS\homes\yehuai\资源";
    public bool StorageManagementEnabled { get; set; } = false;
    public int LocalHotBudgetGB { get; set; } = 600;
    public int LocalReserveGB { get; set; } = 400;
    public int ArchiveReserveGB { get; set; } = 450;
    public int MigrationBatchGB { get; set; } = 30;
    public int StorageCheckMinutes { get; set; } = 15;
    public int MigrationParallelism { get; set; } = 3;
    public string[] PinnedLocalModels { get; set; } = [];
    public string DownloadCategory { get; set; } = LibraryPaths.DefaultCategory;
    public string[] LibraryCategories { get; set; } = [LibraryPaths.DefaultCategory, "COS", "微密圈"];
    public string[] LegacyDownloadRoots { get; set; } = [];
    public string BaiduPcsPath { get; set; } = "";
    public string SevenZipPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string RemoteRoot { get; set; } = "/xiuren-auto";
    public int DownloadParallelism { get; set; } = 2;
    public int SingleFileParallelism { get; set; } = 10;
    public bool UseSystemProxy { get; set; } = false;
    public bool LowSpeedGuardEnabled { get; set; } = true;
    public int LowSpeedThresholdKBps { get; set; } = 512;
    public int LowSpeedSeconds { get; set; } = 180;
    public int LowSpeedRetryCount { get; set; } = 2;
    public int LogRetentionDays { get; set; } = 30;
    public int LogMaxTotalMB { get; set; } = 100;
    public int LogMaxFileMB { get; set; } = LogMaintenance.DefaultMaxFileMB;
    public double SlideshowSeconds { get; set; } = 5;
    public bool SkipCompleted { get; set; } = true;
    public bool DeleteArchiveAfterExtract { get; set; } = true;
    public bool KeepSidecarFiles { get; set; } = false;
    public string[] ImageExts { get; set; } = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".avif"];
    public string[] VideoExts { get; set; } = [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".flv", ".webm", ".ts"];
    public string[] ArchiveExts { get; set; } = [".zip", ".rar", ".7z", ".gz", ".tar", ".001", ".wim", ".iso"];

    public static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public static Settings Load()
    {
        AppPaths.Ensure();
        var s = File.Exists(AppPaths.SettingsFile)
            ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile, Encoding.UTF8), JsonOptions) ?? new Settings()
            : new Settings();
        s.DetectTools();
        s.Save();
        LogMaintenance.Cleanup(s);
        return s;
    }

    public Settings Snapshot()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ??
               throw new InvalidOperationException("Unable to create settings snapshot.");
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
    }

    private void DetectTools()
    {
        if (string.IsNullOrWhiteSpace(BaiduPcsPath)) BaiduPcsPath = FindEnv("BAIDUPCS_GO");
        if (string.IsNullOrWhiteSpace(SevenZipPath)) SevenZipPath = FindEnv("SEVENZIP_EXE");
        if (string.IsNullOrWhiteSpace(SevenZipPath))
        {
            foreach (var p in new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" })
                if (File.Exists(p)) { SevenZipPath = p; break; }
        }
        if (string.IsNullOrWhiteSpace(FfprobePath) || !File.Exists(FfprobePath))
        {
            var adjacent = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");
            if (File.Exists(adjacent))
                FfprobePath = adjacent;
            else
            {
                foreach (var ffmpegRoot in new[]
                         {
                             Path.Combine(AppPaths.ProgramRoot, "tools", "ffmpeg"),
                             Path.Combine(AppPaths.ToolRoot, "tools", "ffmpeg")
                         }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var portable = Path.Combine(ffmpegRoot, "bin", "ffprobe.exe");
                    if (File.Exists(portable))
                    {
                        FfprobePath = portable;
                        break;
                    }
                    if (!Directory.Exists(ffmpegRoot)) continue;
                    FfprobePath = Directory.EnumerateFiles(
                        ffmpegRoot,
                        "ffprobe.exe",
                        SearchOption.AllDirectories).FirstOrDefault() ?? "";
                    if (!string.IsNullOrWhiteSpace(FfprobePath)) break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(DownloadRoot)) DownloadRoot = AppPaths.LibraryRoot;
        DownloadRoot = NormalizeConfiguredPath(DownloadRoot, AppPaths.LibraryRoot);
        ArchiveRoot = NormalizeConfiguredPath(ArchiveRoot, "");
        LocalHotBudgetGB = Math.Clamp(LocalHotBudgetGB, 50, 3500);
        LocalReserveGB = Math.Clamp(LocalReserveGB, 50, 2000);
        ArchiveReserveGB = Math.Clamp(ArchiveReserveGB, 50, 2000);
        MigrationBatchGB = Math.Clamp(MigrationBatchGB, 1, 200);
        StorageCheckMinutes = Math.Clamp(StorageCheckMinutes, 2, 1440);
        MigrationParallelism = Math.Clamp(MigrationParallelism, 1, 8);
        PinnedLocalModels = (PinnedLocalModels ?? [])
            .Select(x => XiurenClient.Safe(x.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DownloadCategory = LibraryPaths.DefaultCategory;
        LibraryCategories = LibraryPaths.Categories(this).ToArray();
        LibraryCategories = (LibraryCategories ?? [])
            .Append(DownloadCategory)
            .Select(LibraryPaths.NormalizeCategory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LegacyDownloadRoots = (LegacyDownloadRoots ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Path.GetFullPath(x.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FindEnv(string name)
    {
        foreach (var v in new[] { Environment.GetEnvironmentVariable(name), Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User), Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) })
            if (!string.IsNullOrWhiteSpace(v) && File.Exists(v)) return v;
        return "";
    }

    private static string NormalizeConfiguredPath(string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd('\\');
        }
        catch
        {
            return fallback;
        }
    }
}

internal sealed class Database
{
    private static readonly object SaveGate = new();
    public List<ResourceItem> Resources { get; set; } = [];
    public List<JobItem> Jobs { get; set; } = [];
    public List<LocalStat> LocalFiles { get; set; } = [];

    public static Database Load()
    {
        AppPaths.Ensure();
        if (!File.Exists(AppPaths.DbFile)) return new Database();
        var database = JsonSerializer.Deserialize<Database>(
            File.ReadAllText(AppPaths.DbFile, Encoding.UTF8),
            Settings.JsonOptions) ?? new Database();
        foreach (var item in database.Resources)
        {
            item.Category = LibraryPaths.NormalizeCategory(item.Category);
            if (!string.IsNullOrWhiteSpace(item.DetectedCategory))
                item.DetectedCategory = LibraryPaths.NormalizeCategory(item.DetectedCategory);
        }
        foreach (var job in database.Jobs)
            job.DownloadCategory = LibraryPaths.NormalizeCategory(job.DownloadCategory);
        foreach (var item in database.LocalFiles)
            item.Category = LibraryPaths.NormalizeCategory(item.Category);
        return database;
    }

    public void Save()
    {
        lock (SaveGate)
        {
            var temp = AppPaths.DbFile + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(this, Settings.JsonOptions),
                Encoding.UTF8);
            File.Move(temp, AppPaths.DbFile, true);
        }
    }

    public ResourceItem Upsert(ResourceItem item)
    {
        var old = Resources.FirstOrDefault(x => x.DetailUrl.Equals(item.DetailUrl, StringComparison.OrdinalIgnoreCase));
        if (old == null) { Resources.Add(item); return item; }
        old.PostId = item.PostId;
        old.Title = item.Title;
        old.Model = item.Model;
        old.Category = LibraryPaths.NormalizeCategory(item.Category);
        old.CategorySource = item.CategorySource;
        old.DetectedCategory = item.DetectedCategory;
        old.PanUrl = item.PanUrl;
        old.PanPassword = item.PanPassword;
        old.ExtractPassword = item.ExtractPassword;
        old.ResourceType = item.ResourceType;
        old.Status = item.Status;
        old.LastChecked = item.LastChecked;
        return old;
    }
}

internal sealed class ResourceItem
{
    public string PostId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Model { get; set; } = "";
    public string Category { get; set; } = LibraryPaths.DefaultCategory;
    public string CategorySource { get; set; } = "";
    public string DetectedCategory { get; set; } = "";
    public string DetailUrl { get; set; } = "";
    public string PanUrl { get; set; } = "";
    public string PanPassword { get; set; } = "";
    public string ExtractPassword { get; set; } = "";
    public string ResourceType { get; set; } = "Photo";
    public string Status { get; set; } = "";
    public string DownloadStatus { get; set; } = "";
    public string ExtractStatus { get; set; } = "";
    public string LocalDir { get; set; } = "";
    public string Error { get; set; } = "";
    public string LastChecked { get; set; } = "";
}

internal sealed class JobItem
{
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public string Aliases { get; set; } = "";
    public string Exclusions { get; set; } = "";
    public int Pages { get; set; } = 999;
    public int MaxReady { get; set; } = 9999;
    public string SearchMode { get; set; } = "Global";
    public string CategoryPath { get; set; } = "/tbgx";
    public string DownloadCategory { get; set; } = LibraryPaths.DefaultCategory;
    public string Status { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Error { get; set; } = "";
    public string StartedAt { get; set; } = DateTime.Now.ToString("s");
    public string FinishedAt { get; set; } = "";
}

internal sealed class LocalStat
{
    public string Category { get; set; } = LibraryPaths.DefaultCategory;
    public string Model { get; set; } = "";
    public string Title { get; set; } = "";
    public string LocalDir { get; set; } = "";
    public string StorageTier { get; set; } = StorageTiers.Local;
    public int ImageCount { get; set; }
    public int VideoCount { get; set; }
    public int InvalidVideoCount { get; set; }
    public long TotalBytes { get; set; }
    public string LastScanned { get; set; } = "";
}

internal static class StorageTiers
{
    public const string Local = "本地";
    public const string Archive = "NAS";
}

internal sealed class ModelStat
{
    public string Category { get; set; } = LibraryPaths.DefaultCategory;
    public string Model { get; set; } = "";
    public int SetCount { get; set; }
    public int ImageCount { get; set; }
    public int VideoCount { get; set; }
    public int InvalidVideoCount { get; set; }
    public long TotalBytes { get; set; }
    public int FailedCount { get; set; }
}

internal sealed class XiurenClient
{
    private readonly Settings settings;
    private readonly HttpClient directHttp;
    private readonly HttpClient proxyHttp;
    private HttpClient http;
    private bool usingProxy;
    private bool loggedIn;
    private IProgress<string>? networkLog;
    private readonly SemaphoreSlim routeGate = new(1, 1);

    public XiurenClient(Settings settings)
    {
        this.settings = settings;
        directHttp = CreateHttpClient(false);
        proxyHttp = CreateHttpClient(true);
        usingProxy = settings.UseSystemProxy;
        http = usingProxy ? proxyHttp : directHttp;
    }

    private static HttpClient CreateHttpClient(bool useSystemProxy)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = useSystemProxy
        };
        if (useSystemProxy)
            handler.Proxy = ReadWindowsProxy() ?? HttpClient.DefaultProxy;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 XiurenDownloader/1.0");
        return client;
    }

    private static IWebProxy? ReadWindowsProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (Convert.ToInt32(key?.GetValue("ProxyEnable", 0), CultureInfo.InvariantCulture) != 1)
                return null;

            var raw = Convert.ToString(key?.GetValue("ProxyServer", ""), CultureInfo.InvariantCulture)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (raw.Contains('='))
            {
                var entries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split('=', 2))
                    .Where(x => x.Length == 2)
                    .ToDictionary(x => x[0].Trim(), x => x[1].Trim(), StringComparer.OrdinalIgnoreCase);
                raw = entries.GetValueOrDefault("https")
                    ?? entries.GetValueOrDefault("http")
                    ?? entries.Values.FirstOrDefault()
                    ?? "";
            }
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!raw.Contains("://", StringComparison.Ordinal))
                raw = "http://" + raw;
            return new WebProxy(raw, true);
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<ResourceItem>> SearchAsync(
        string keyword,
        int pages,
        int maxReady,
        IProgress<string> log,
        CancellationToken ct,
        Func<string, ResourceItem?>? findSaved = null,
        Action<ResourceItem>? saveReady = null,
        IReadOnlyCollection<string>? exclusions = null)
    {
        networkLog = log;
        await LoginAsync(ct);
        var posts = new List<(string title, string url)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= Math.Max(1, pages); page++)
        {
            var url = BuildSearchUrl(keyword, page);
            log.Report("读取搜索页 " + page + ": " + url);
            string html;
            try
            {
                html = await GetStringWithContextAsync(url, "读取搜索页 " + page, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                log.Report("搜索页 " + page + " 不存在，已到最后一页");
                break;
            }
            var found = ParsePosts(html);
            if (found.Count == 0) break;
            foreach (var post in found)
            {
                var excludedBy = MatchExclusion(post.title, exclusions);
                if (!string.IsNullOrWhiteSpace(excludedBy))
                {
                    log.Report($"已按排除规则跳过: {post.title}（命中: {excludedBy}）");
                    continue;
                }
                if (seen.Add(post.url)) posts.Add(post);
            }
            await Task.Delay(500, ct);
        }

        var ready = new List<ResourceItem>();
        var consecutiveDetailFailures = 0;
        foreach (var post in posts)
        {
            var saved = findSaved?.Invoke(post.url);
            if (saved != null && !string.IsNullOrWhiteSpace(saved.PanUrl))
            {
                var excludedBy = MatchExclusion(saved.Title, exclusions);
                if (!string.IsNullOrWhiteSpace(excludedBy))
                {
                    log.Report($"已按排除规则跳过已入库资源: {saved.Title}（命中: {excludedBy}）");
                    continue;
                }
                if (await EnsureWebsiteCategoryAsync(saved, log, ct))
                    saveReady?.Invoke(saved);
                log.Report("使用已入库链接: " + saved.Title);
                ready.Add(saved);
                if (maxReady > 0 && ready.Count >= maxReady) break;
                continue;
            }

            log.Report("读取详情: " + post.title);
            ResourceItem item;
            try
            {
                item = await ReadDetailAsync(post.title, post.url, ct);
                consecutiveDetailFailures = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.Report("跳过详情读取失败: " + post.title + " | " + ErrorText.Format(ex).Replace(Environment.NewLine, " | "));
                consecutiveDetailFailures++;
                if (consecutiveDetailFailures >= 5)
                {
                    log.Report("连续 5 个详情读取失败，停止抓取并下载本轮已入库资源。");
                    break;
                }
                continue;
            }

            var detailExcludedBy = MatchExclusion(item.Title, exclusions);
            if (!string.IsNullOrWhiteSpace(detailExcludedBy))
            {
                log.Report($"已按排除规则跳过详情: {item.Title}（命中: {detailExcludedBy}）");
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.PanUrl)) { log.Report("跳过无网盘链接资源: " + item.Title); continue; }
            ready.Add(item);
            saveReady?.Invoke(item);
            if (maxReady > 0 && ready.Count >= maxReady) break;
            await Task.Delay(500, ct);
        }
        return ready;
    }

    public async Task<ResourceItem> RefreshDetailAsync(ResourceItem item, CancellationToken ct)
    {
        await LoginAsync(ct);
        return await ReadDetailAsync(item.Title, item.DetailUrl, ct);
    }

    private async Task<bool> EnsureWebsiteCategoryAsync(
        ResourceItem item,
        IProgress<string> log,
        CancellationToken ct)
    {
        if (item.CategorySource.Equals(SiteCategoryClassifier.WebsiteSource, StringComparison.OrdinalIgnoreCase) ||
            item.CategorySource.Equals(SiteCategoryClassifier.DefaultSource, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var html = await GetStringWithContextAsync(item.DetailUrl, "识别已入库资源分类", ct);
            var detection = SiteCategoryClassifier.Detect(html);
            item.Category = detection.Category;
            item.CategorySource = detection.IsDetected
                ? SiteCategoryClassifier.WebsiteSource
                : SiteCategoryClassifier.DefaultSource;
            item.DetectedCategory = detection.IsDetected ? detection.Category : "";
            item.LastChecked = DateTime.Now.ToString("s");
            ReportResolvedCategory(log, item.Title, detection);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.Report("已入库资源分类识别失败，保留原分类: " + item.Title + " | " + ex.Message);
            return false;
        }
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        await routeGate.WaitAsync(ct);
        try
        {
            if (loggedIn) return;

            Exception? firstError = null;
            for (var routeAttempt = 0; routeAttempt < 2; routeAttempt++)
            {
                try
                {
                    await LoginCurrentRouteAsync(ct);
                    loggedIn = true;
                    networkLog?.Report("网站网络通道: " + RouteName());
                    return;
                }
                catch (Exception ex) when (ShouldRetry(ex, ct) && routeAttempt == 0)
                {
                    firstError = ex;
                    SwitchRoute();
                    networkLog?.Report("当前网络通道失败，自动切换为: " + RouteName());
                }
                catch (Exception ex) when (IsRequestFailure(ex, ct))
                {
                    throw new InvalidOperationException(BuildDualRouteError("网站登录", LoginUrl(), firstError, ex), ex);
                }
            }
        }
        finally
        {
            routeGate.Release();
        }
    }

    private async Task LoginCurrentRouteAsync(CancellationToken ct)
    {
        var url = LoginUrl();
        using var req = CreateLoginRequest(url);
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("status", out var status) || status.ToString() != "1")
            throw new InvalidOperationException("网站登录失败: " + text);
    }

    private string LoginUrl() => settings.BaseUrl.TrimEnd('/') + "/wp-admin/admin-ajax.php";

    private HttpRequestMessage CreateLoginRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "user_login",
                ["username"] = settings.UserName,
                ["password"] = settings.Password,
                ["rememberme"] = "1"
            })
        };
        req.Headers.Referrer = new Uri(settings.BaseUrl.TrimEnd('/') + "/");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        return req;
    }

    private string BuildSearchUrl(string keyword, int page)
    {
        var q = Uri.EscapeDataString(keyword);
        if (settings.SearchMode.Equals("Category", StringComparison.OrdinalIgnoreCase))
        {
            var path = settings.CategoryPath.Trim();
            if (!path.StartsWith('/')) path = "/" + path;
            path = path.TrimEnd('/');
            return page <= 1 ? $"{settings.BaseUrl.TrimEnd('/')}{path}?s={q}" : $"{settings.BaseUrl.TrimEnd('/')}{path}/page/{page}/?s={q}";
        }
        return page <= 1 ? $"{settings.BaseUrl.TrimEnd('/')}/?s={q}" : $"{settings.BaseUrl.TrimEnd('/')}/page/{page}/?s={q}";
    }

    private async Task<ResourceItem> ReadDetailAsync(string fallbackTitle, string url, CancellationToken ct)
    {
        var html = await GetStringWithContextAsync(url, "读取详情页", ct);
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " "));
        var h1 = Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var title = h1.Success ? Clean(h1.Groups[1].Value) : fallbackTitle;
        var category = SiteCategoryClassifier.Detect(html);
        ReportResolvedCategory(networkLog, title, category);
        var postId = Regex.Match(url, @"/(\d+)\.html").Groups[1].Value;
        var pan = ExtractPanUrl(html);
        if (string.IsNullOrWhiteSpace(pan) && !string.IsNullOrWhiteSpace(postId))
            pan = await ResolvePanFromDownloadAsync(await AjaxDownloadAsync(postId, url, ct), ct);
        pan = WebUtility.HtmlDecode(pan).TrimEnd('.', ',', ';', '，', '。', '；', ')');

        var pwd = Query(pan, "pwd");
        if (string.IsNullOrWhiteSpace(pwd))
        {
            var m = Regex.Match(text, @"(?:\u63D0\u53D6(?:\u5BC6\u7801|\u7801)|\u6587\u4EF6\u5BC6\u7801|\u8BBF\u95EE\u7801|\u5206\u4EAB\u5BC6\u7801)\s*[:：]?\s*([A-Za-z0-9]{4})");
            if (m.Success) pwd = m.Groups[1].Value;
        }

        var extract = ExtractArchivePassword(text);

        return new ResourceItem
        {
            PostId = postId,
            Title = title,
            Model = ModelName(title),
            Category = category.Category,
            CategorySource = category.IsDetected
                ? SiteCategoryClassifier.WebsiteSource
                : SiteCategoryClassifier.DefaultSource,
            DetectedCategory = category.IsDetected ? category.Category : "",
            DetailUrl = url,
            PanUrl = pan,
            PanPassword = pwd,
            ExtractPassword = extract,
            ResourceType = IsVideo(title) ? "Video" : "Photo",
            Status = string.IsNullOrWhiteSpace(pan) ? "MissingPan" : "Ready",
            LastChecked = DateTime.Now.ToString("s")
        };
    }

    internal static string ExtractArchivePassword(string text)
    {
        var match = Regex.Match(
            text ?? "",
            @"(?:解压密码|解压码)\s*[】\]\):：]?\s*([^\s，。；;,<>()（）]+(?:\s*(?:或|/|\|)\s*[^\s，。；;,<>()（）]+)?)",
            RegexOptions.IgnoreCase);
        var value = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("教程", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("说明", StringComparison.OrdinalIgnoreCase))
        {
            return "taotudao.com 或 www.taotudao.com";
        }
        return value;
    }

    private static void ReportResolvedCategory(
        IProgress<string>? log,
        string title,
        SiteCategoryDetection detection)
    {
        if (detection.IsDetected)
        {
            log?.Report($"网站分类: {detection.Category} - {title}");
            return;
        }

        var reason = detection.HasConflict
            ? "详情页同时出现微密圈和 COS 标记"
            : "详情页未匹配微密圈或 COS";
        log?.Report($"{reason}，按规则归入{LibraryPaths.DefaultCategory}: {title}");
    }

    private static void ReportCategory(
        IProgress<string>? log,
        string title,
        SiteCategoryDetection detection)
    {
        if (detection.IsDetected)
        {
            log?.Report($"网站分类: {detection.Category} - {title}");
            return;
        }

        var reason = detection.HasConflict
            ? "详情页出现冲突分类 " + string.Join("/", detection.Signals)
            : "详情页没有受支持的分类标记";
        log?.Report($"{reason}，使用未识别时分类 {detection.Category}: {title}");
    }

    private async Task<string> ResolvePanFromDownloadAsync(string raw, CancellationToken ct)
    {
        var pan = ExtractPanUrl(raw);
        if (!string.IsNullOrWhiteSpace(pan)) return pan;

        var go = Regex.Match(raw, @"(?:https?://[^""'<>]+)?/go\?post_id=\d+", RegexOptions.IgnoreCase).Value;
        if (string.IsNullOrWhiteSpace(go)) return "";

        var goUrl = Uri.TryCreate(go, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : settings.BaseUrl.TrimEnd('/') + go;
        var goHtml = await GetStringWithContextAsync(goUrl, "解析站内下载跳转", ct);
        return ExtractPanUrl(goHtml);
    }

    private static string ExtractPanUrl(string html)
    {
        return Regex.Match(WebUtility.HtmlDecode(html ?? ""), @"https?://pan\.baidu\.com/s/[^\s""'<>]+", RegexOptions.IgnoreCase).Value;
    }

    private async Task<string> AjaxDownloadAsync(string postId, string referer, CancellationToken ct)
    {
        var url = settings.BaseUrl.TrimEnd('/') + "/wp-admin/admin-ajax.php";
        var raw = await SendWithContextAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["action"] = "user_down_ajax", ["post_id"] = postId })
            };
            req.Headers.Referrer = new Uri(referer);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            return req;
        }, "获取会员下载链接", url, ct);
        try { using var doc = JsonDocument.Parse(raw); return doc.RootElement.TryGetProperty("msg", out var msg) ? msg.ToString() : raw; } catch { return raw; }
    }

    private async Task<string> GetStringWithContextAsync(string url, string operation, CancellationToken ct)
    {
        Exception? firstError = null;
        for (var routeAttempt = 0; routeAttempt < 2; routeAttempt++)
        {
            try
            {
                return await http.GetStringAsync(url, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                throw;
            }
            catch (Exception ex) when (ShouldRetry(ex, ct) && routeAttempt == 0)
            {
                firstError = ex;
                await SwitchRouteAndLoginAsync(ct);
            }
            catch (Exception ex) when (IsRequestFailure(ex, ct))
            {
                throw new InvalidOperationException(BuildDualRouteError(operation, url, firstError, ex), ex);
            }
        }
        throw new InvalidOperationException(operation + "失败: 系统代理和直连均不可用");
    }

    private async Task<string> SendWithContextAsync(Func<HttpRequestMessage> requestFactory, string operation, string url, CancellationToken ct)
    {
        Exception? firstError = null;
        for (var routeAttempt = 0; routeAttempt < 2; routeAttempt++)
        {
            using var req = requestFactory();
            try
            {
                using var resp = await http.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{operation}失败: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}{Environment.NewLine}{url}");
                return text;
            }
            catch (Exception ex) when (ShouldRetry(ex, ct) && routeAttempt == 0)
            {
                firstError = ex;
                await SwitchRouteAndLoginAsync(ct);
            }
            catch (Exception ex) when (IsRequestFailure(ex, ct))
            {
                throw new InvalidOperationException(BuildDualRouteError(operation, url, firstError, ex), ex);
            }
        }
        throw new InvalidOperationException(operation + "失败: 系统代理和直连均不可用");
    }

    private async Task SwitchRouteAndLoginAsync(CancellationToken ct)
    {
        await routeGate.WaitAsync(ct);
        try
        {
            SwitchRoute();
            networkLog?.Report("网络请求失败，自动切换为: " + RouteName());
            await LoginCurrentRouteAsync(ct);
            loggedIn = true;
            networkLog?.Report("网络通道切换成功: " + RouteName());
        }
        finally
        {
            routeGate.Release();
        }
    }

    private void SwitchRoute()
    {
        usingProxy = !usingProxy;
        http = usingProxy ? proxyHttp : directHttp;
        loggedIn = false;
    }

    private string RouteName() => usingProxy ? "系统代理" : "直连";

    private static string BuildDualRouteError(string operation, string url, Exception? first, Exception second)
    {
        var firstText = first == null ? "未尝试" : ErrorText.Format(first).Replace(Environment.NewLine, " | ");
        var secondText = ErrorText.Format(second).Replace(Environment.NewLine, " | ");
        return $"{operation}失败: 系统代理和直连均不可用{Environment.NewLine}首次: {firstText}{Environment.NewLine}切换后: {secondText}{Environment.NewLine}{url}";
    }

    private static bool ShouldRetry(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (ex is HttpRequestException) return true;
        if (ex is TaskCanceledException) return true;
        return ex is InvalidOperationException ioe && ioe.Message.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequestFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        return ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException;
    }

    private static string BuildRequestError(string operation, string url, Exception ex)
    {
        var reason = ex is TaskCanceledException ? "请求超时或连接被中断" : ErrorText.Format(ex);
        return $"{operation}失败: {reason}{Environment.NewLine}{url}";
    }

    private static List<(string title, string url)> ParsePosts(string html)
    {
        var list = new List<(string, string)>();
        var pattern = "<h2[^>]*class=\"[^\"]*entry-title[^\"]*\"[^>]*>\\s*<a[^>]+href=\"([^\"]+)\"[^>]*>(.*?)</a>";
        foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
            list.Add((Clean(m.Groups[2].Value), WebUtility.HtmlDecode(m.Groups[1].Value)));
        return list;
    }

    private static string Clean(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", "")), "\\s+", " ").Trim();
    private static string Query(string url, string key) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && x[0] == key).Select(x => Uri.UnescapeDataString(x[1])).FirstOrDefault() ?? "" : "";
    public static List<string> SearchNames(string canonicalName, string aliases) =>
        new[] { canonicalName }
            .Concat(SplitTerms(aliases))
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    public static List<string> ExclusionTerms(string exclusions) =>
        SplitTerms(exclusions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    public static string MatchExclusion(string? value, IReadOnlyCollection<string>? exclusions) =>
        exclusions?.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x) &&
            (value ?? "").Contains(x, StringComparison.OrdinalIgnoreCase)) ?? "";
    private static IEnumerable<string> SplitTerms(string? value) =>
        (value ?? "")
            .Split([',', '，', ';', '；', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
    public static string Safe(string name) { foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_'); return name.Trim(); }
    public static string ModelName(string title)
    {
        foreach (var p in new[]
        {
            @"No\.\d+\s+(.+?)\s*\[[^\]]+\]",
            @"(?:NO|VOL)\.?\d+\s+(.+?)(?:\[[^\]]*|\s*$)",
            @"\]\s*\d{4}\.\d{2}\.\d{2}\s+(.+?)\s*\[[^\]]+\]",
            @"\]\s*(.+?)\s*\[[^\]]+\]",
            @"^(.+?)\s*[–—-]\s*.+?(?:原版写真|写真|\[[^\]]+\])",
            @"^(.+?)《.+?》.*?(?:原版写真|写真|\[[^\]]+\])",
            @"^([\u4e00-\u9fa5A-Za-z0-9_]+)\s+.+?(?:原版写真|写真|\[[^\]]+\])"
        })
        {
            var m = Regex.Match(title, p, RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var name = Regex.Replace(m.Groups[1].Value, @"^(?:NO|VOL)\.?\d+\s+", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^\d{1,4}[.．]\s*", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"^(?:鱼子酱Fish（私拍）|鱼子酱Fish|私拍)\s*[-–—]\s*(?:NO\.\d+\s*&\s*)?", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, @"[-_\s]+$", "").Trim();
            name = Regex.Replace(name, @"[（(].*?[）)]", "").Trim();
            if (name.Contains('&') || name.Contains('＆')) name = name.Split(['&', '＆'], StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            name = NormalizeModelAlias(name);
            if (name.Length is > 0 and <= 24)
                return Safe(name);
        }
        return "unknown";
    }

    public static string NormalizeModelAlias(string name)
    {
        name = Safe((name ?? "").Trim());
        name = Regex.Replace(name, @"^\d{1,4}[.．]\s*", "", RegexOptions.IgnoreCase).Trim();
        if (name.Equals("杏子", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("杏子Yada", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("杏子 –", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("杏子-", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("杏子—", StringComparison.OrdinalIgnoreCase))
            return "杏子Yada";
        if (name.Equals("小薯条", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("小薯条", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("小薯条nienie", StringComparison.OrdinalIgnoreCase))
            return "小薯条nienie";
        return name;
    }
    private static bool IsVideo(string title) => Regex.IsMatch(title, @"\b(mp4|视频|剧情|分钟)\b", RegexOptions.IgnoreCase);
}

internal sealed record DownloadRunResult(
    int TotalGroups,
    int CompletedGroups,
    int FailedGroups,
    int DeferredGroups)
{
    public static DownloadRunResult Empty { get; } = new(0, 0, 0, 0);
    public bool IsComplete => FailedGroups == 0 && DeferredGroups == 0;
}

internal sealed class Downloader
{
    private readonly Settings settings;
    private readonly Database db;
    private readonly IProgress<string> log;
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private readonly object progressLogGate = new();
    private DateTime lastProgressLog = DateTime.MinValue;

    private sealed class CandidateGroup
    {
        public string Key { get; set; } = "";
        public string Category { get; set; } = LibraryPaths.DefaultCategory;
        public string Model { get; set; } = "";
        public string Title { get; set; } = "";
        public List<ResourceItem> Items { get; set; } = [];
        public int LowSpeedRetryCount { get; set; }
    }

    private sealed class LowSpeedDownloadException(string message) : Exception(message);

    public Downloader(Settings settings, Database db, IProgress<string> log)
    {
        this.settings = settings;
        this.db = db;
        this.log = log;
    }

    public async Task<DownloadRunResult> RunAsync(
        IEnumerable<ResourceItem> resources,
        CancellationToken ct)
    {
        Need(settings.BaiduPcsPath, "BaiduPCS-Go");
        Need(settings.SevenZipPath, "7-Zip");
        var items = resources.Where(x => !string.IsNullOrWhiteSpace(x.PanUrl)).ToList();
        if (items.Count == 0) return DownloadRunResult.Empty;
        var groups = BuildCandidateGroups(items);

        var parallel = Math.Clamp(settings.DownloadParallelism, 1, 5);
        var workerConfigs = PrepareWorkerConfigs(parallel);
        var saveGate = new object();
        log.Report("资源并发数: " + parallel);
        log.Report("单文件下载线程数: " + Math.Clamp(settings.SingleFileParallelism, 1, 20));

        await Proc(settings.BaiduPcsPath, ["who"], AppPaths.ToolRoot, ct, configDir: workerConfigs[0]);
        await Proc(settings.BaiduPcsPath, ["mkdir", settings.RemoteRoot], AppPaths.ToolRoot, ct, true, workerConfigs[0]);

        var pendingGroups = new ConcurrentQueue<CandidateGroup>(groups);
        var workerCount = Math.Min(parallel, groups.Count);
        var workers = Enumerable.Range(0, workerCount).Select(async workerIndex =>
        {
            var configDir = workerConfigs[workerIndex];
            while (!ct.IsCancellationRequested && pendingGroups.TryDequeue(out var group))
            {
                try
                {
                    await ProcessCandidateGroupAsync(group, configDir, saveGate, ct);
                }
                catch (LowSpeedDownloadException ex)
                {
                    if (group.LowSpeedRetryCount < Math.Max(0, settings.LowSpeedRetryCount))
                    {
                        group.LowSpeedRetryCount++;
                        pendingGroups.Enqueue(group);
                        log.Report($"慢任务已排到队尾: {group.Title}（第 {group.LowSpeedRetryCount}/{settings.LowSpeedRetryCount} 次重连）");
                    }
                    else
                    {
                        foreach (var item in group.Items)
                        {
                            item.DownloadStatus = "Ready";
                            item.ExtractStatus = "";
                            item.Error = ex.Message + "；本轮已延后，下次继续队列时会续传。";
                        }
                        lock (saveGate) db.Save();
                        log.Report($"慢任务本轮延后: {group.Title}；已保留部分文件，下次继续队列时续传。");
                    }
                }
            }
        });
        await Task.WhenAll(workers);

        var completedGroups = groups.Count(x => x.Items.Any(item =>
            item.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase)));
        var failedGroups = groups.Count(x => x.Items.All(item =>
            item.DownloadStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase)));
        var deferredGroups = groups.Count - completedGroups - failedGroups;
        log.Report(
            $"下载阶段结束：候选组 {groups.Count} 组，成功 {completedGroups} 组，失败 {failedGroups} 组，待继续 {deferredGroups} 组。");
        return new DownloadRunResult(
            groups.Count,
            completedGroups,
            failedGroups,
            deferredGroups);
    }

    private async Task ProcessCandidateGroupAsync(CandidateGroup group, string configDir, object saveGate, CancellationToken ct)
    {
        var modelDir = LibraryPaths.ModelRoot(settings, group.Category, group.Model);
        Directory.CreateDirectory(modelDir);

        var existingDir = FindExistingMediaDir(modelDir, group.Title);
        if (settings.SkipCompleted && existingDir != null)
        {
            foreach (var item in group.Items)
            {
                var duplicateDir = Path.Combine(modelDir, XiurenClient.Safe(item.Title));
                if (!duplicateDir.Equals(existingDir, StringComparison.OrdinalIgnoreCase))
                    DeleteIfNoMedia(duplicateDir);
                item.LocalDir = existingDir;
                item.DownloadStatus = "Downloaded";
                item.ExtractStatus = "Extracted";
                item.Error = "";
            }
            lock (saveGate) db.Save();
            log.Report($"跳过已完成: {group.Title}（候选链接 {group.Items.Count} 个）");
            return;
        }

        var titleDir = Path.Combine(modelDir, XiurenClient.Safe(group.Title));
        foreach (var item in OrderCandidates(group.Items))
        {
            log.Report($"尝试候选链接: {item.Title} -> {item.DetailUrl}");
            await ProcessOneAsync(item, configDir, saveGate, ct, group.Title);
            if (item.DownloadStatus == "Downloaded")
            {
                foreach (var candidate in group.Items)
                {
                    candidate.LocalDir = item.LocalDir;
                    candidate.DownloadStatus = "Downloaded";
                    candidate.ExtractStatus = "Extracted";
                    candidate.Error = "";
                }
                lock (saveGate) db.Save();
                log.Report($"候选组完成: {group.Title}");
                return;
            }

            DeleteIfNoMedia(titleDir);
            log.Report($"候选链接失败，继续尝试下一个: {item.Title}");
        }

        var errors = string.Join("；", group.Items.Where(x => !string.IsNullOrWhiteSpace(x.Error)).Select(x => x.Error).Distinct().Take(5));
        foreach (var item in group.Items)
        {
            item.DownloadStatus = "Failed";
            item.ExtractStatus = "";
            if (string.IsNullOrWhiteSpace(item.Error))
                item.Error = string.IsNullOrWhiteSpace(errors) ? "同编号候选链接全部失败。" : errors;
        }
        lock (saveGate) db.Save();
        log.Report($"候选组全部失败: {group.Title} | 候选链接 {group.Items.Count} 个");
    }

    private async Task ProcessOneAsync(ResourceItem item, string configDir, object saveGate, CancellationToken ct, string? canonicalTitle = null)
    {
        var modelDir = LibraryPaths.ModelRoot(settings, item.Category, item.Model);
        var workTitle = canonicalTitle ?? item.Title;
        var defaultTitleDir = Path.Combine(modelDir, XiurenClient.Safe(workTitle));
        var titleDir = ResolveWorkingTitleDir(item, defaultTitleDir);
        item.LocalDir = titleDir;
        Directory.CreateDirectory(modelDir);

        var existingDir = FindExistingMediaDir(modelDir, workTitle);
        if (settings.SkipCompleted && existingDir != null)
        {
            if (!titleDir.Equals(existingDir, StringComparison.OrdinalIgnoreCase))
                DeleteIfNoMedia(titleDir);
            item.LocalDir = existingDir;
            item.DownloadStatus = "Downloaded";
            item.ExtractStatus = "Extracted";
            item.Error = "";
            log.Report("跳过已完成: " + item.Title);
            return;
        }

        try
        {
            Directory.CreateDirectory(titleDir);
            if (VideoValidator.HasInvalidMarker(titleDir))
            {
                var removed = VideoValidator.DeleteMarkedInvalidFiles(titleDir);
                DeleteArchives(titleDir);
                log.Report($"准备重新下载，已移除确认损坏的视频: {removed} 个 - {item.Title}");
            }
            if (HasArchives(titleDir))
            {
                log.Report("发现本地压缩包，先尝试解压: " + item.Title);
                await RefreshMissingPanPasswordAsync(item, ct);
                await FinalizeLocalFiles(item, titleDir, ct);
                return;
            }

            log.Report("转存并下载: " + item.Title);
            await RefreshMissingPanPasswordAsync(item, ct);
            var remoteItemDir = RemoteItemDir(item, workTitle);
            await Proc(settings.BaiduPcsPath, ["mkdir", remoteItemDir], AppPaths.ToolRoot, ct, true, configDir);
            await Proc(settings.BaiduPcsPath, ["cd", remoteItemDir], AppPaths.ToolRoot, ct, true, configDir);
            var fileParallelism = Math.Clamp(settings.SingleFileParallelism, 1, 20);
            await Proc(settings.BaiduPcsPath, ["config", "set", "-max_parallel", fileParallelism.ToString(CultureInfo.InvariantCulture), "-max_download_load", "1", "-savedir", titleDir.Replace('\\', '/')], AppPaths.ToolRoot, ct, true, configDir);
            var partialFiles = GetIncompleteDownloadFiles(titleDir);
            if (partialFiles.Count > 0)
            {
                log.Report("发现未完成文件，直接从已转存网盘目录续传: " + item.Title);
                foreach (var partialFile in partialFiles)
                {
                    var remoteFile = CombineRemote(remoteItemDir, Path.GetFileName(partialFile));
                    var partialDir = Path.GetDirectoryName(partialFile)!;
                    log.Report($"续传文件: {remoteFile} -> {partialDir}");
                    await Proc(settings.BaiduPcsPath, ["d", remoteFile, "--saveto", partialDir.Replace('\\', '/')], AppPaths.ToolRoot, ct, configDir: configDir, watchLowSpeed: true);
                    if (HasArchives(titleDir) || HasMedia(titleDir)) break;
                }
            }
            else
            {
                var args = new List<string> { "transfer", item.PanUrl };
                args.Add(string.IsNullOrWhiteSpace(item.PanPassword) ? "" : item.PanPassword);
                args.Add("--download");
                await Proc(settings.BaiduPcsPath, args, AppPaths.ToolRoot, ct, allowFail: true, configDir: configDir, watchLowSpeed: true);
            }
            if (!HasArchives(titleDir) && !HasMedia(titleDir))
            {
                log.Report("本地未发现完整文件，从网盘目录补下载: " + remoteItemDir);
                await Proc(settings.BaiduPcsPath, ["d", remoteItemDir, "--saveto", titleDir.Replace('\\', '/'), "--ow"], AppPaths.ToolRoot, ct, configDir: configDir, watchLowSpeed: true);
            }
            if (!HasArchives(titleDir) && !HasMedia(titleDir))
                throw new InvalidOperationException("网盘转存/下载失败：没有下载到任何文件，可能是分享链接失效或页面不存在。");

            await FinalizeLocalFiles(item, titleDir, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LowSpeedDownloadException ex)
        {
            item.DownloadStatus = "Ready";
            item.ExtractStatus = "";
            item.Error = ex.Message;
            lock (saveGate) db.Save();
            throw;
        }
        catch (Exception ex)
        {
            item.DownloadStatus = "Failed";
            item.Error = ex.Message;
            DeleteInvalidMediaFiles(titleDir);
            DeleteIfEmpty(titleDir);
            log.Report("失败: " + item.Title + " | " + ex.Message);
        }
        lock (saveGate) db.Save();
    }

    private async Task RefreshMissingPanPasswordAsync(ResourceItem item, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.PanPassword) || string.IsNullOrWhiteSpace(item.DetailUrl)) return;
        try
        {
            var refreshed = await new XiurenClient(settings).RefreshDetailAsync(item, ct);
            var changed = false;
            if (!string.IsNullOrWhiteSpace(refreshed.PanUrl) && !refreshed.PanUrl.Equals(item.PanUrl, StringComparison.OrdinalIgnoreCase))
            {
                item.PanUrl = refreshed.PanUrl;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(refreshed.PanPassword))
            {
                item.PanPassword = refreshed.PanPassword;
                changed = true;
                log.Report("已刷新文件密码: " + item.Title + " -> " + item.PanPassword);
            }
            if (!string.IsNullOrWhiteSpace(refreshed.ExtractPassword) && !refreshed.ExtractPassword.Equals(item.ExtractPassword, StringComparison.OrdinalIgnoreCase))
            {
                item.ExtractPassword = refreshed.ExtractPassword;
                changed = true;
            }
            if (changed)
            {
                item.Status = "Ready";
                item.LastChecked = DateTime.Now.ToString("s");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.Report("刷新详情失败，继续使用已有链接: " + item.Title + " | " + ErrorText.Format(ex).Replace(Environment.NewLine, " | "));
        }
    }

    private List<CandidateGroup> BuildCandidateGroups(List<ResourceItem> items)
    {
        return items
            .GroupBy(
                x => LibraryPaths.NormalizeCategory(x.Category) + "|" +
                     XiurenClient.Safe(x.Model) + "|" +
                     ResourceKey(x.Title),
                StringComparer.OrdinalIgnoreCase)
            .Select(g => new CandidateGroup
            {
                Key = g.Key,
                Category = LibraryPaths.NormalizeCategory(g.First().Category),
                Model = g.First().Model,
                Title = ChooseCanonicalTitle(g.ToList()),
                Items = g.ToList()
            })
            .ToList();
    }

    private static IEnumerable<ResourceItem> OrderCandidates(IEnumerable<ResourceItem> items)
    {
        return items
            .OrderBy(x => x.DownloadStatus == "Failed" ? 1 : 0)
            .ThenByDescending(x => DateTime.TryParse(x.LastChecked, out var dt) ? dt : DateTime.MinValue)
            .ThenBy(x => x.Title.Length)
            .ToList();
    }

    private static string ChooseCanonicalTitle(List<ResourceItem> items)
    {
        return items
            .OrderBy(x => Regex.IsMatch(x.Title, @"[／/]\s*\d+(?:\.\d+)?\s*(?:MB|GB)|\d+(?:\.\d+)?\s*(?:MB|GB)", RegexOptions.IgnoreCase) ? 1 : 0)
            .ThenBy(x => x.Title.Length)
            .Select(x => x.Title)
            .First();
    }

    private async Task FinalizeLocalFiles(ResourceItem item, string titleDir, CancellationToken ct)
    {
        await ExtractionGate.WaitAsync(ct);
        try
        {
            NormalizeUnknownFileExtensions(titleDir);
            MoveSingleFolderUp(titleDir);
            NormalizeUnknownFileExtensions(titleDir);
            RenameArchives(titleDir, item.Title);
            await ExtractAsync(titleDir, item.ExtractPassword, ct);
            DeleteInvalidMediaFiles(titleDir);
            FlattenMediaFiles(titleDir);
            CleanSidecars(titleDir);
            MoveSingleFolderUp(titleDir);
            FlattenMediaFiles(titleDir);
            var invalidVideos = FindInvalidVideos(titleDir, ct);
            if (invalidVideos.Count > 0)
            {
                VideoValidator.WriteInvalidMarker(titleDir, invalidVideos);
                var names = string.Join("、", invalidVideos.Select(x => Path.GetFileName(x.Path)).Take(3));
                throw new InvalidOperationException($"发现损坏视频 {invalidVideos.Count} 个，已保留现场并等待重新下载: {names}");
            }
            VideoValidator.ClearInvalidMarker(titleDir);
            if (settings.DeleteArchiveAfterExtract) DeleteArchives(titleDir);
            CleanSidecars(titleDir);
            if (!HasMedia(titleDir)) throw new InvalidOperationException("解压后未发现可用图片或视频");
        }
        finally
        {
            ExtractionGate.Release();
        }

        item.DownloadStatus = "Downloaded";
        item.ExtractStatus = "Extracted";
        item.Error = "";
    }

    private static HashSet<string> Snapshot(string dir) => Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);

    private void Arrange(string modelDir, string titleDir, HashSet<string> before)
    {
        Directory.CreateDirectory(titleDir);
        foreach (var entry in Directory.EnumerateFileSystemEntries(modelDir).Where(x => !before.Contains(x) && !x.Equals(titleDir, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var target = Path.Combine(titleDir, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target)) target = Path.Combine(titleDir, Path.GetFileNameWithoutExtension(entry) + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + Path.GetExtension(entry));
            if (File.Exists(entry)) File.Move(entry, target);
            else if (Directory.Exists(entry)) Directory.Move(entry, target);
        }
    }

    private void RenameArchives(string dir, string title)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsArchive))
        {
            if (IsMultiPartArchive(f)) continue;
            var ext = f.EndsWith(".7z.gz", StringComparison.OrdinalIgnoreCase) ? ".7z.gz" : Path.GetExtension(f);
            var target = Path.Combine(Path.GetDirectoryName(f)!, XiurenClient.Safe(title) + ext);
            if (!f.Equals(target, StringComparison.OrdinalIgnoreCase) && !File.Exists(target)) File.Move(f, target);
        }
    }

    private void FlattenMediaFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        NormalizeUnknownFileExtensions(dir);
        var mediaExts = settings.ImageExts.Concat(settings.VideoExts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !Path.GetDirectoryName(f)!.Equals(dir, StringComparison.OrdinalIgnoreCase) && mediaExts.Contains(Path.GetExtension(f))).ToList())
        {
            var target = Path.Combine(dir, Path.GetFileName(f));
            if (File.Exists(target))
                target = Path.Combine(dir, Path.GetFileNameWithoutExtension(f) + "_" + Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(f));
            Try(() => File.Move(f, target));
        }

        foreach (var child in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            Try(() =>
            {
                if (!Directory.EnumerateFileSystemEntries(child).Any())
                    Directory.Delete(child, false);
            });
    }

    private async Task ExtractAsync(string dir, string passwords, CancellationToken ct)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 5; pass++)
        {
            DeleteZeroByteArchives(dir);
            DeleteInvalidMediaFiles(dir);
            var archives = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(IsArchiveStart)
                .Where(f => new FileInfo(f).Length > 0)
                .Where(f => !processed.Contains(f))
                .OrderBy(x => x)
                .ToList();
            if (archives.Count == 0) break;

            var extractedAny = false;
            foreach (var archive in archives)
            {
                var file = archive;
                if (archive.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && !archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    var inner = archive[..^3];
                    if (!File.Exists(inner))
                    {
                        using var input = File.OpenRead(archive);
                        using var gzip = new GZipStream(input, CompressionMode.Decompress);
                        using var output = File.Create(inner);
                        await gzip.CopyToAsync(output, ct);
                    }
                    file = inner;
                }

                var ok = false;
                var inputName = Path.GetRelativePath(dir, file);
                foreach (var password in Passwords(passwords))
                {
                    DeleteZeroByteArchives(dir);
                    DeleteInvalidMediaFiles(dir);
                    var args = new List<string> { "x", "-y", "-aoa", "-o.", inputName, "-p" + password };
                    if (await Proc(settings.SevenZipPath, args, dir, ct, true) == 0) { ok = true; break; }
                }
                if (!ok) throw new InvalidOperationException("解压失败: " + Path.GetFileName(file));
                processed.Add(archive);
                processed.Add(file);
                extractedAny = true;
            }

            MoveSingleFolderUp(dir);
            if (!extractedAny) break;
        }
        MoveSingleFolderUp(dir);
        DeleteZeroByteArchives(dir);
        DeleteInvalidMediaFiles(dir);
    }

    private IEnumerable<string> Passwords(string value)
    {
        var list = new List<string>();
        foreach (var p in (value ?? "").Split(["或", "|", "/", ";", ",", "，"], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().Trim('】', ']', '：', ':')).Where(x => x.Length > 0 && !x.Equals("教程", StringComparison.OrdinalIgnoreCase) && !x.Equals("说明", StringComparison.OrdinalIgnoreCase)))
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        foreach (var p in new[]
                 {
                     "shenye001.com", "www.shenye001.com",
                     "www.sosiba.vip", "sosiba.vip",
                     "taotudao.com", "www.taotudao.com"
                 })
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list;
    }

    private void CleanSidecars(string dir)
    {
        if (settings.KeepSidecarFiles) return;
        NormalizeUnknownFileExtensions(dir);
        var mediaExts = settings.ImageExts.Concat(settings.VideoExts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumerateUserFiles(dir).Where(f =>
                     !Path.GetFileName(f).Equals(VideoValidator.InvalidMarkerName, StringComparison.OrdinalIgnoreCase) &&
                     !mediaExts.Contains(Path.GetExtension(f)) &&
                     !IsArchive(f)))
            Try(() => File.Delete(f));
    }

    private void NormalizeUnknownFileExtensions(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in EnumerateUserFiles(dir).Where(f => !IsDownloadMarker(f) && !File.Exists(f + ".BaiduPCS-Go-downloading")).ToList())
        {
            if (!string.IsNullOrWhiteSpace(Path.GetExtension(f))) continue;
            var ext = GuessExtension(f);
            if (string.IsNullOrWhiteSpace(ext)) continue;
            var target = f + ext;
            if (File.Exists(target))
                target = Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileName(f) + "_" + Guid.NewGuid().ToString("N")[..6] + ext);
            Try(() => File.Move(f, target));
        }
    }

    private List<VideoValidationResult> FindInvalidVideos(string dir, CancellationToken ct)
    {
        var invalid = new List<VideoValidationResult>();
        foreach (var f in EnumerateUserFiles(dir).Where(IsVideoFile).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var result = VideoValidator.Check(f, settings, sampleFrames: true, ct);
            if (!result.IsValid) invalid.Add(result);
        }
        if (invalid.Count > 0) log.Report($"视频完整性检查失败: {invalid.Count} 个 - {Path.GetFileName(dir)}");
        return invalid;
    }

    private void DeleteArchives(string dir)
    {
        foreach (var f in EnumerateUserFiles(dir).Where(IsArchive))
            Try(() => File.Delete(f));
    }

    private void DeleteZeroByteArchives(string dir)
    {
        foreach (var f in EnumerateUserFiles(dir).Where(IsArchive).Where(f => new FileInfo(f).Length == 0))
            Try(() => File.Delete(f));
    }

    private void DeleteInvalidMediaFiles(string dir)
    {
        foreach (var f in EnumerateUserFiles(dir)
                     .Where(IsConfiguredMediaExtension)
                     .Where(f => !IsMediaFile(f)))
            Try(() => File.Delete(f));
    }

    private void DeleteIfEmpty(string dir)
    {
        if (!Directory.Exists(dir)) return;
        Try(() =>
        {
            if (!Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, true);
        });
    }

    private void DeleteIfNoMedia(string dir)
    {
        if (!Directory.Exists(dir) || HasMedia(dir) || HasIncompleteDownloads(dir) || AppPaths.IsInsideTool(dir)) return;
        Try(() => Directory.Delete(dir, true));
    }

    public int CleanDownloadRoot()
    {
        var deleted = 0;
        var keepMedia = settings.ImageExts.Concat(settings.VideoExts).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in EnumerateUserFiles(settings.DownloadRoot)
                     .Where(f => !IsDownloadMarker(f) && !File.Exists(f + ".BaiduPCS-Go-downloading"))
                     .Where(f => !Path.GetFileName(f).Equals(VideoValidator.InvalidMarkerName, StringComparison.OrdinalIgnoreCase))
                     .Where(f => !keepMedia.Contains(Path.GetExtension(f))))
        {
            Try(() => { File.Delete(f); deleted++; });
        }
        return deleted;
    }

    private IEnumerable<string> EnumerateUserFiles(string root)
    {
        if (!Directory.Exists(root) || AppPaths.IsInsideTool(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !AppPaths.IsInsideTool(f)).ToList();
    }

    private bool IsArchive(string f)
    {
        if (f.EndsWith(".BaiduPCS-Go-downloading", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(f + ".BaiduPCS-Go-downloading"))
            return false;
        var name = Path.GetFileName(f);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(f)))
            return GuessExtension(f) is ".zip" or ".rar" or ".7z" or ".gz" or ".wim" or ".iso";
        if (name.EndsWith(".7z.gz", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(name, @"\.(?:7z|zip|rar)\.\d{3}$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(name, @"\.part\d+\.rar$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(name, @"\.r\d{2}$", RegexOptions.IgnoreCase)) return true;
        return settings.ArchiveExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);
    }

    private bool IsArchiveStart(string f)
    {
        var name = Path.GetFileName(f);
        if (Regex.IsMatch(name, @"\.(?:7z|zip|rar)\.(?!001$)\d{3}$", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(name, @"\.part(?!0*1\.rar$)\d+\.rar$", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(name, @"\.r\d{2}$", RegexOptions.IgnoreCase)) return false;
        return IsArchive(f);
    }

    private static bool IsMultiPartArchive(string f)
    {
        var name = Path.GetFileName(f);
        return Regex.IsMatch(name, @"\.(?:7z|zip|rar)\.\d{3}$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(name, @"\.part\d+\.rar$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(name, @"\.r\d{2}$", RegexOptions.IgnoreCase);
    }
    private static bool IsDownloadMarker(string f) =>
        f.EndsWith(".BaiduPCS-Go-downloading", StringComparison.OrdinalIgnoreCase);

    private List<string> GetIncompleteDownloadFiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return EnumerateUserFiles(dir)
            .Where(f => !IsDownloadMarker(f) && File.Exists(f + ".BaiduPCS-Go-downloading"))
            .Where(f => new FileInfo(f).Length > 0)
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();
    }

    private string ResolveWorkingTitleDir(ResourceItem item, string defaultTitleDir)
    {
        if (!string.IsNullOrWhiteSpace(item.LocalDir) &&
            HasIncompleteDownloads(item.LocalDir))
            return item.LocalDir;
        return defaultTitleDir;
    }

    private bool HasIncompleteDownloads(string dir) => GetIncompleteDownloadFiles(dir).Count > 0;

    private bool HasMedia(string dir) => Directory.Exists(dir) &&
        EnumerateUserFiles(dir).Any(f =>
            !File.Exists(f + ".BaiduPCS-Go-downloading") &&
            IsMediaFile(f));
    private bool HasArchives(string dir) => Directory.Exists(dir) && EnumerateUserFiles(dir).Any(IsArchive);

    private string RemoteItemDir(ResourceItem item, string workTitle)
    {
        var raw = !string.IsNullOrWhiteSpace(item.PostId) ? item.PostId : ResourceKey(workTitle);
        var stem = Regex.Replace(raw, @"[^\w.-]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(stem)) stem = "item";
        if (stem.Length > 40) stem = stem[..40];
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(item.DetailUrl + "|" + workTitle)))[..10].ToLowerInvariant();
        return CombineRemote(settings.RemoteRoot, stem + "-" + hash);
    }

    private static string CombineRemote(string root, string child)
    {
        root = string.IsNullOrWhiteSpace(root) ? "/" : root.TrimEnd('/');
        if (!root.StartsWith('/')) root = "/" + root;
        return root + "/" + child.Trim('/');
    }

    private bool IsMediaFile(string f)
    {
        var ext = Path.GetExtension(f);
        if (string.IsNullOrWhiteSpace(ext))
            return GuessExtension(f) is ".mp4" or ".jpg" or ".png" or ".webp" or ".gif";
        if (settings.ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return MediaFileValidator.QuickImageHeaderLooksValid(f);
        if (settings.VideoExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return IsValidVideoFile(f);
        return false;
    }

    private bool IsConfiguredMediaExtension(string f)
    {
        var ext = Path.GetExtension(f);
        return settings.ImageExts.Contains(ext, StringComparer.OrdinalIgnoreCase) ||
               settings.VideoExts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsVideoFile(string f) => settings.VideoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    private bool IsValidVideoFile(string f)
        => string.IsNullOrWhiteSpace(Path.GetExtension(f))
            ? GuessExtension(f) == ".mp4"
            : VideoValidator.QuickHeaderLooksValid(f);

    private static string GuessExtension(string f)
    {
        try
        {
            using var stream = File.OpenRead(f);
            var length = (int)Math.Min(stream.Length, 64);
            if (length < 4) return "";
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            if (read >= 6 && buffer[0] == 0x37 && buffer[1] == 0x7A && buffer[2] == 0xBC && buffer[3] == 0xAF && buffer[4] == 0x27 && buffer[5] == 0x1C) return ".7z";
            if (read >= 4 && buffer[0] == (byte)'P' && buffer[1] == (byte)'K') return ".zip";
            if (read >= 7 && buffer[0] == (byte)'R' && buffer[1] == (byte)'a' && buffer[2] == (byte)'r' && buffer[3] == (byte)'!') return ".rar";
            if (read >= 2 && buffer[0] == 0x1F && buffer[1] == 0x8B) return ".gz";
            if (read >= 5 && buffer[0] == (byte)'M' && buffer[1] == (byte)'S' && buffer[2] == (byte)'W' && buffer[3] == (byte)'I' && buffer[4] == (byte)'M') return ".wim";
            if (read >= 12 && Encoding.ASCII.GetString(buffer, 4, Math.Min(8, read - 4)).Contains("ftyp", StringComparison.Ordinal)) return ".mp4";
            if (read >= 3 && buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF) return ".jpg";
            if (read >= 8 && buffer[0] == 0x89 && buffer[1] == (byte)'P' && buffer[2] == (byte)'N' && buffer[3] == (byte)'G') return ".png";
            if (read >= 12 && buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F' &&
                buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P') return ".webp";
            if (read >= 6 && buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F') return ".gif";
        }
        catch
        {
            return "";
        }
        return "";
    }

    private string? FindExistingMediaDir(string modelDir, string title)
    {
        if (!Directory.Exists(modelDir)) return null;
        foreach (var file in Directory.GetFiles(modelDir, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsMediaFile(file) && LooseMediaMatchesTitle(file, title))
                return modelDir;
        }
        var key = ResourceKey(title);
        foreach (var dir in Directory.GetDirectories(modelDir).Where(x => !AppPaths.IsInsideTool(x)))
        {
            if (!VideoValidator.HasInvalidMarker(dir) &&
                HasMedia(dir) &&
                (Path.GetFileName(dir).Equals(XiurenClient.Safe(title), StringComparison.OrdinalIgnoreCase) || ResourceKey(Path.GetFileName(dir)) == key))
                return dir;
        }
        return null;
    }

    internal static bool LooseMediaMatchesTitle(string file, string title)
    {
        var fileKey = NormalizeLooseMediaName(Path.GetFileNameWithoutExtension(file));
        var titleKey = NormalizeLooseMediaName(title);
        if (fileKey.Length < 6 || titleKey.Length < 6) return false;
        return titleKey.Contains(fileKey, StringComparison.OrdinalIgnoreCase) ||
               fileKey.Contains(titleKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLooseMediaName(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "");

    private static string ResourceKey(string title)
    {
        var m = Regex.Match(title, @"\b(?:NO\.?|No\.?|VOL\.?)\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.TrimStart('0');
        return Regex.Replace(title, @"[\s\[\]【】（）()／/_.\-]+", "", RegexOptions.IgnoreCase).ToUpperInvariant();
    }

    private static void MoveSingleFolderUp(string dir)
    {
        var files = Directory.GetFiles(dir);
        var dirs = Directory.GetDirectories(dir);
        if (files.Length != 0 || dirs.Length != 1) return;
        foreach (var child in Directory.EnumerateFileSystemEntries(dirs[0]))
        {
            var target = Path.Combine(dir, Path.GetFileName(child));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (File.Exists(child)) File.Move(child, target);
            else Directory.Move(child, target);
        }
        Try(() => Directory.Delete(dirs[0], true));
    }

    private static List<string> PrepareWorkerConfigs(int count)
    {
        var source = Environment.GetEnvironmentVariable("BAIDUPCS_GO_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(source)) source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BaiduPCS-Go");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("找不到 BaiduPCS-Go 配置目录: " + source);
        var root = Path.Combine(AppPaths.DataDir, "baidupcs-workers");
        Directory.CreateDirectory(root);
        CleanupOldWorkerConfigs(root);
        var runRoot = Path.Combine(root, DateTime.Now.ToString("yyyyMMddHHmmssfff") + "-" + Environment.ProcessId);
        Directory.CreateDirectory(runRoot);
        var result = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var target = Path.Combine(runRoot, "worker-" + i);
            CopyDir(source, target);
            result.Add(target);
        }
        return result;
    }

    private static void CleanupOldWorkerConfigs(string root)
    {
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-2))
                    Directory.Delete(dir, true);
            }
            catch
            {
                // 旧下载进程可能仍占用配置文件；跳过即可，下次再清理。
            }
        }
    }

    private static void CopyDir(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
    }

    private static void Need(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到 " + name, path);
    }

    private static void Try(Action action) { try { action(); } catch { } }

    private sealed class DownloadSpeedWatch
    {
        private readonly object gate = new();
        private readonly Queue<(DateTime Time, double SpeedKBps)> samples = new();
        private double lastSpeedKBps;
        private string lastLine = "";
        public bool Killed { get; set; }

        public void Observe(double kbps, string line, Settings settings)
        {
            lock (gate)
            {
                lastSpeedKBps = kbps;
                lastLine = line.Trim();
                if (!settings.LowSpeedGuardEnabled || settings.LowSpeedThresholdKBps <= 0 || settings.LowSpeedSeconds <= 0)
                {
                    samples.Clear();
                    return;
                }
                var now = DateTime.Now;
                samples.Enqueue((now, kbps));
                var cutoff = now.AddSeconds(-settings.LowSpeedSeconds);
                while (samples.Count > 0 && samples.Peek().Time < cutoff)
                    samples.Dequeue();
            }
        }

        public bool ShouldKill(Settings settings, out string message)
        {
            lock (gate)
            {
                if (Killed || samples.Count < 5 ||
                    (DateTime.Now - samples.Peek().Time).TotalSeconds < settings.LowSpeedSeconds)
                {
                    message = "";
                    return false;
                }
                var average = samples.Average(x => x.SpeedKBps);
                var lowRatio = samples.Count(x => x.SpeedKBps < settings.LowSpeedThresholdKBps) / (double)samples.Count;
                if (average >= settings.LowSpeedThresholdKBps || lowRatio < 0.7)
                {
                    message = "";
                    return false;
                }
                message = $"下载速度在 {settings.LowSpeedSeconds} 秒窗口内平均仅 {average:0.##}KB/s（{lowRatio:P0} 的采样低于 {settings.LowSpeedThresholdKBps}KB/s），准备重连并排到队尾。最后速度: {lastSpeedKBps:0.##}KB/s | {lastLine}";
                Killed = true;
                return true;
            }
        }
    }

    private async Task<int> Proc(string exe, IReadOnlyList<string> args, string cwd, CancellationToken ct, bool allowFail = false, string? configDir = null, bool watchLowSpeed = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(configDir)) psi.Environment["BAIDUPCS_GO_CONFIG_DIR"] = configDir;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        var baiduTransferError = "";
        var speedWatch = watchLowSpeed ? new DownloadSpeedWatch() : null;
        var lowSpeedError = "";
        process.OutputDataReceived += (_, e) => ReportProcessLine(e.Data, ref baiduTransferError, speedWatch);
        process.ErrorDataReceived += (_, e) => ReportProcessLine(e.Data, ref baiduTransferError, speedWatch);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var watchdog = speedWatch == null ? Task.CompletedTask : Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                if (process.HasExited) return;
                if (!speedWatch.ShouldKill(settings, out var message)) continue;
                lowSpeedError = message;
                log.Report(message);
                try { process.Kill(entireProcessTree: true); } catch { }
                return;
            }
        }, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }
        try { await watchdog.ConfigureAwait(false); } catch { }
        if (!string.IsNullOrWhiteSpace(lowSpeedError) && !allowFail)
            throw new LowSpeedDownloadException(lowSpeedError);
        if (!string.IsNullOrWhiteSpace(baiduTransferError) && !allowFail)
            throw new InvalidOperationException("网盘转存/下载失败：" + baiduTransferError);
        if (process.ExitCode != 0 && !allowFail) throw new InvalidOperationException(Path.GetFileName(exe) + " 退出码 " + process.ExitCode);
        return process.ExitCode;
    }

    private void ReportProcessLine(string? line, ref string baiduTransferError, DownloadSpeedWatch? speedWatch)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (speedWatch != null && TryParseSpeedKBps(line, out var kbps))
            speedWatch.Observe(kbps, line, settings);
        if (IsNoisyProgressLine(line))
        {
            lock (progressLogGate)
            {
                if ((DateTime.Now - lastProgressLog).TotalSeconds < 10) return;
                lastProgressLog = DateTime.Now;
            }
            log.Report("下载进度: " + line.Trim());
            return;
        }
        if (line.Contains("分享链接转存到网盘失败") || line.Contains("页面不存在") || line.Contains("提取码错误") || line.Contains("分享不存在"))
            baiduTransferError = line.Trim();
        log.Report(line);
    }

    private static bool TryParseSpeedKBps(string line, out double kbps)
    {
        kbps = 0;
        var m = Regex.Match(line, @"(?<value>\d+(?:\.\d+)?)(?<unit>B|KB|MB|GB)/s", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        var value = double.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
        kbps = m.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => value / 1024,
            "KB" => value,
            "MB" => value * 1024,
            "GB" => value * 1024 * 1024,
            _ => value
        };
        return true;
    }

    private static bool IsNoisyProgressLine(string line)
    {
        if (line.Contains("........") && line.Contains(" left ", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(line, @"\d+(?:\.\d+)?(?:KB|MB|GB)/\d+(?:\.\d+)?(?:KB|MB|GB)", RegexOptions.IgnoreCase)) return true;
        return false;
    }
}

internal sealed class MainForm : Form
{
    private Settings settings = Settings.Load();
    private Database db = Database.Load();
    private CancellationTokenSource? cts;
    private bool processingQueue;
    private bool stopRequested;
    private bool videoScanRunning;
    private bool refreshingGrids;
    private readonly ConcurrentQueue<string> pendingLogs = new();
    private readonly System.Windows.Forms.Timer logFlushTimer = new() { Interval = 500 };
    private DateTime nextLogCleanup = DateTime.UtcNow.AddHours(1);
    private TabControl tabs = null!;
    private TabPage libraryPage = null!;
    private MediaLibraryView libraryView = null!;
    private TextBox query = null!, aliasQuery = null!, log = null!, baseUrl = null!, user = null!, pass = null!, category = null!, root = null!, baidu = null!, seven = null!, ffprobe = null!;
    private NumericUpDown pages = null!, max = null!, parallel = null!, singleFileParallel = null!;
    private ComboBox mode = null!;
    private DataGridView resources = null!, stats = null!, details = null!, jobs = null!;
    private CheckBox delArchive = null!, skipDone = null!, keepSidecar = null!, useSystemProxy = null!;

    public MainForm()
    {
        Text = "写真自动下载工具";
        Text = "写真资源管理器";
        Width = 1320;
        Height = 840;
        MinimumSize = new Size(1040, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9);
        BuildUi();
        ModernTheme.Apply(this);
        LoadSettingsToUi();
        logFlushTimer.Tick += (_, _) => FlushLogs();
        logFlushTimer.Start();
        MarkStaleRunningJobs();
        RefreshGrids();
    }

    private void BuildUi()
    {
        tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 7),
            Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold)
        };
        Controls.Add(tabs);

        var searchPage = new TabPage("搜索下载");
        tabs.TabPages.Add(searchPage);
        var top = new Panel { Dock = DockStyle.Top, Height = 116 };
        searchPage.Controls.Add(top);
        top.Controls.Add(Label("搜索内容", 12, 14, 70));
        query = TextBox(top, 85, 12, 220, "");
        top.Controls.Add(Label("页数", 320, 14, 40));
        pages = Number(top, 360, 12, 1, 999, 999);
        top.Controls.Add(Label("最大套数", 440, 14, 70));
        max = Number(top, 510, 12, 0, 9999, 9999);
        mode = new ComboBox { Location = new Point(610, 11), Width = 135, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(["全站搜索", "分类内搜索"]);
        top.Controls.Add(mode);
        top.Controls.Add(Label("模特别名", 12, 48, 70));
        aliasQuery = TextBox(top, 85, 45, 660, "");
        aliasQuery.PlaceholderText = "多个别名使用逗号分隔";
        top.Controls.Add(Button("搜索入库", 12, 79, 95, async (_, _) => await Job("Search")));
        top.Controls.Add(Button("搜索并下载", 118, 79, 110, async (_, _) => await Job("SearchDownload")));
        top.Controls.Add(Button("下载就绪项", 240, 79, 105, async (_, _) => await Job("DownloadReady")));
        top.Controls.Add(Button("停止", 360, 79, 75, (_, _) => StopQueue()));
        top.Controls.Add(Button("刷新", 450, 79, 75, (_, _) => RefreshGrids()));
        top.Controls.Add(Button("删除选中资源", 540, 79, 105, (_, _) => DeleteSelectedResources()));
        resources = Grid();
        searchPage.Controls.Add(resources);

        var jobPage = new TabPage("任务队列");
        tabs.TabPages.Add(jobPage);
        jobs = Grid();
        jobPage.Controls.Add(jobs);
        var jobTop = new Panel { Dock = DockStyle.Top, Height = 42 };
        jobTop.Controls.Add(Button("删除选中", 12, 6, 90, (_, _) => DeleteSelectedJobs()));
        jobTop.Controls.Add(Button("清空已结束", 112, 6, 100, (_, _) => ClearFinishedJobs()));
        jobTop.Controls.Add(Button("继续队列", 222, 6, 90, (_, _) => _ = ContinueQueueAsync()));
        jobTop.Controls.Add(Button("清空全部", 322, 6, 90, (_, _) => ClearAllJobs()));
        jobPage.Controls.Add(jobTop);

        var statPage = new TabPage("统计");
        tabs.TabPages.Add(statPage);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        statPage.Controls.Add(split);
        var statTop = new Panel { Dock = DockStyle.Top, Height = 42 };
        statTop.Controls.Add(Button("扫描统计", 12, 6, 95, (_, _) => Scan()));
        statTop.Controls.Add(Button("检查视频完整性", 118, 6, 125, async (_, _) => await CheckVideosAsync()));
        statTop.Controls.Add(Button("应用内浏览", 255, 6, 105, (_, _) => OpenSelectedSetInLibrary()));
        statTop.Controls.Add(Button("打开套图目录", 370, 6, 115, (_, _) => OpenSelectedSetFolder()));
        statPage.Controls.Add(statTop);
        stats = Grid();
        details = Grid();
        split.Panel1.Controls.Add(stats);
        split.Panel2.Controls.Add(details);
        stats.SelectionChanged += (_, _) => RefreshDetails();
        stats.CellDoubleClick += (_, _) => OpenSelectedModelFolder();
        details.CellDoubleClick += (_, _) => OpenSelectedSetInLibrary();

        libraryPage = new TabPage("图库收藏");
        tabs.TabPages.Add(libraryPage);
        libraryView = new MediaLibraryView(db, settings, Log);
        libraryPage.Controls.Add(libraryView);

        var cleanPage = new TabPage("清理工具");
        tabs.TabPages.Add(cleanPage);
        cleanPage.Controls.Add(Button("清理下载目录非图片/视频文件", 16, 20, 220, (_, _) =>
        {
            SaveSettings();
            var deleted = new Downloader(settings, db, Progress()).CleanDownloadRoot();
            Log("清理完成，删除 " + deleted + " 个文件。");
            Scan();
        }));
        cleanPage.Controls.Add(Label("清理时会自动跳过 _Tool 工具目录。", 16, 65, 500));

        var settingsPage = new TabPage("设置");
        tabs.TabPages.Add(settingsPage);
        BuildSettings(settingsPage);

        var logPage = new TabPage("日志");
        tabs.TabPages.Add(logPage);
        log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = new Font("Consolas", 9) };
        logPage.Controls.Add(log);
    }

    private void BuildSettings(Control page)
    {
        var y = 18;
        page.Controls.Add(Label("网站地址", 16, y, 90)); baseUrl = TextBox(page, 120, y, 520, ""); y += 36;
        page.Controls.Add(Label("网站账号", 16, y, 90)); user = TextBox(page, 120, y, 260, ""); y += 36;
        page.Controls.Add(Label("网站密码", 16, y, 90)); pass = TextBox(page, 120, y, 260, ""); pass.UseSystemPasswordChar = true; y += 36;
        page.Controls.Add(Label("分类路径", 16, y, 90)); category = TextBox(page, 120, y, 180, ""); y += 36;
        page.Controls.Add(Label("下载目录", 16, y, 90)); root = TextBox(page, 120, y, 520, ""); y += 36;
        page.Controls.Add(Label("BaiduPCS-Go", 16, y, 90)); baidu = TextBox(page, 120, y, 620, ""); y += 36;
        page.Controls.Add(Label("7-Zip", 16, y, 90)); seven = TextBox(page, 120, y, 620, ""); y += 36;
        page.Controls.Add(Label("ffprobe", 16, y, 90)); ffprobe = TextBox(page, 120, y, 620, ""); y += 36;
        page.Controls.Add(Label("资源并发数", 16, y, 90)); parallel = Number(page, 120, y, 1, 5, 2);
        page.Controls.Add(Label("单文件线程", 240, y, 85)); singleFileParallel = Number(page, 330, y, 1, 20, 10);
        page.Controls.Add(Label("普通百度用户设为1，SVIP建议5", 410, y, 230)); y += 36;
        delArchive = CheckBox(page, "解压后删除压缩包", 120, y, 170);
        skipDone = CheckBox(page, "跳过已完成", 310, y, 120);
        keepSidecar = CheckBox(page, "保留附带文件", 450, y, 130);
        useSystemProxy = CheckBox(page, "优先系统代理（失败自动切换）", 560, y, 220);
        y += 40;
        page.Controls.Add(Button("保存设置", 120, y, 100, (_, _) => { SaveSettings(); MessageBox.Show("已保存"); }));
    }

    private Task Job(string type)
    {
        SaveSettings();
        var job = new JobItem
        {
            Type = type,
            Target = type == "DownloadReady" ? "全部就绪项" : query.Text.Trim(),
            Aliases = type == "DownloadReady" ? "" : aliasQuery.Text.Trim(),
            Pages = (int)pages.Value,
            MaxReady = (int)max.Value,
            SearchMode = settings.SearchMode,
            CategoryPath = settings.CategoryPath,
            Status = "Queued",
            StartedAt = DateTime.Now.ToString("s"),
            FinishedAt = ""
        };
        db.Jobs.Insert(0, job);
        db.Save();
        RefreshGrids();
        Log("已加入任务队列: " + JobLabel(job));
        _ = RunJobQueueAsync();
        return Task.CompletedTask;
    }

    private async Task RunJobQueueAsync()
    {
        if (processingQueue) return;
        processingQueue = true;
        stopRequested = false;
        try
        {
            while (!stopRequested)
            {
                var job = db.Jobs.LastOrDefault(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase));
                if (job == null) break;

                cts = new CancellationTokenSource();
                job.Status = "Running";
                job.Error = "";
                db.Save();
                RefreshGrids();
                Log("开始任务: " + JobLabel(job));

                try
                {
                    await ExecuteJob(job, cts.Token);
                    job.Status = "Done";
                }
                catch (OperationCanceledException)
                {
                    job.Status = "Canceled";
                    Log("任务已停止: " + JobLabel(job));
                }
                catch (Exception ex)
                {
                    var error = ErrorText.Format(ex);
                    job.Status = "Failed";
                    job.Error = error;
                    Log("失败: " + error.Replace(Environment.NewLine, " | "));
                    MessageBox.Show(error, "任务失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    job.FinishedAt = DateTime.Now.ToString("s");
                    db.Save();
                    cts.Dispose();
                    cts = null;
                    RefreshGrids();
                }
            }
        }
        finally
        {
            processingQueue = false;
            stopRequested = false;
        }
    }

    private async Task ContinueQueueAsync()
    {
        if (processingQueue)
        {
            Log("任务队列正在运行。");
            return;
        }

        var job = db.Jobs.FirstOrDefault(x => x.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase));
        if (job == null && !db.Jobs.Any(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)))
            job = db.Jobs.FirstOrDefault(x => x.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase));

        if (job != null)
        {
            job.Status = "Queued";
            job.FinishedAt = "";
            job.Error = "";
            db.Save();
            RefreshGrids();
            Log("已恢复任务: " + JobLabel(job));
        }
        else if (!db.Jobs.Any(x => x.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)))
        {
            var incomplete = IncompleteLocalResources().ToList();
            if (incomplete.Count == 0)
            {
                Log("没有可继续的任务。需要下载现有资源时，请点击“下载就绪项”。");
                return;
            }

            db.Jobs.Insert(0, new JobItem
            {
                Type = "ResumeIncomplete",
                Target = "本地未完成下载",
                Status = "Queued",
                StartedAt = DateTime.Now.ToString("s")
            });
            db.Save();
            RefreshGrids();
            Log("已从资源记录恢复本地续传队列：" + incomplete.Count + " 条。");
        }

        await RunJobQueueAsync();
    }

    private async Task ExecuteJob(JobItem job, CancellationToken ct)
    {
        if (job.Type == "Search")
        {
            var items = await Search(job, ct);
            Log("入库 " + items.Count + " 套");
            return;
        }
        if (job.Type == "SearchDownload")
        {
            var items = await Search(job, ct);
            await new Downloader(settings, db, Progress()).RunAsync(items, ct);
            Scan();
            return;
        }
        if (job.Type == "DownloadReady")
        {
            RepairMissingCompletedResources();
            var items = PendingReadyResources().ToList();
            Log("下载就绪未完成项: " + items.Count + " 条");
            await new Downloader(settings, db, Progress()).RunAsync(items, ct);
            Scan();
            return;
        }
        if (job.Type == "DownloadModelReady")
        {
            RepairMissingCompletedResources(job.Target);
            var items = PendingResourcesForModel(job.Target).ToList();
            Log($"下载已入库未完成项: {job.Target}，{items.Count} 条");
            await new Downloader(settings, db, Progress()).RunAsync(items, ct);
            Scan();
            return;
        }
        if (job.Type == "ResumeIncomplete")
        {
            var items = IncompleteLocalResources().ToList();
            Log("续传本地未完成项: " + items.Count + " 条");
            await new Downloader(settings, db, Progress()).RunAsync(items, ct);
            Scan();
            return;
        }
        throw new InvalidOperationException("未知任务类型: " + job.Type);
    }

    private IEnumerable<ResourceItem> PendingResourcesForModel(string modelName)
    {
        var model = XiurenClient.Safe((modelName ?? "").Trim());
        if (string.IsNullOrWhiteSpace(model)) return [];
        return db.Resources.Where(x =>
            x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
            x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
            !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<ResourceItem> PendingReadyResources()
    {
        return db.Resources.Where(x =>
            x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
            !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<ResourceItem> IncompleteLocalResources()
    {
        return db.Resources.Where(x =>
            x.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) &&
            !x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(x.PanUrl) &&
            HasIncompleteLocalDownload(x));
    }

    private static bool HasIncompleteLocalDownload(ResourceItem item)
    {
        const string markerSuffix = ".BaiduPCS-Go-downloading";
        if (string.IsNullOrWhiteSpace(item.LocalDir) || !Directory.Exists(item.LocalDir))
            return false;
        try
        {
            return Directory.EnumerateFiles(
                    item.LocalDir,
                    "*" + markerSuffix,
                    SearchOption.AllDirectories)
                .Any(marker =>
                {
                    var partial = marker[..^markerSuffix.Length];
                    return File.Exists(partial) && new FileInfo(partial).Length > 0;
                });
        }
        catch
        {
            return false;
        }
    }

    private int RepairMissingCompletedResources(string? modelFilter = null)
    {
        var model = XiurenClient.Safe((modelFilter ?? "").Trim());
        var changed = 0;
        foreach (var r in db.Resources.Where(x =>
            x.DownloadStatus.Equals("Downloaded", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(model) || x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))))
        {
            if (HasUsableLocalMedia(r)) continue;
            r.Status = "Ready";
            r.DownloadStatus = "";
            r.ExtractStatus = "";
            r.Error = "本地文件缺失或视频损坏，等待重新下载";
            changed++;
        }
        if (changed > 0)
        {
            db.Save();
            RefreshGrids();
            Log("已把本地缺失/损坏的已完成记录改回待下载: " + changed + " 条");
        }
        return changed;
    }

    private bool HasUsableLocalMedia(ResourceItem r)
    {
        foreach (var dir in CandidateLocalDirs(r))
        {
            if (!Directory.Exists(dir)) continue;
            if (VideoValidator.HasInvalidMarker(dir)) continue;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(x => !AppPaths.IsInsideTool(x));
            if (files.Any(f => IsImageForStats(f) || IsValidVideoForStats(f))) return true;
        }
        if (!string.IsNullOrWhiteSpace(r.Model))
        {
            var modelDir = LibraryPaths.ModelRoot(settings, r.Category, r.Model);
            if (Directory.Exists(modelDir) && Directory.GetFiles(modelDir, "*", SearchOption.TopDirectoryOnly)
                    .Any(f => Downloader.LooseMediaMatchesTitle(f, r.Title) &&
                              (IsImageForStats(f) || IsValidVideoForStats(f))))
                return true;
        }
        return false;
    }

    private IEnumerable<string> CandidateLocalDirs(ResourceItem r)
    {
        if (!string.IsNullOrWhiteSpace(r.LocalDir)) yield return r.LocalDir;
        if (!string.IsNullOrWhiteSpace(r.Model) && !string.IsNullOrWhiteSpace(r.Title))
            yield return LibraryPaths.SetRoot(settings, r.Category, r.Model, r.Title);
    }

    private async Task<List<ResourceItem>> Search(JobItem job, CancellationToken ct)
    {
        settings.SearchMode = string.IsNullOrWhiteSpace(job.SearchMode) ? settings.SearchMode : job.SearchMode;
        settings.CategoryPath = string.IsNullOrWhiteSpace(job.CategoryPath) ? settings.CategoryPath : job.CategoryPath;
        var model = XiurenClient.Safe(job.Target.Trim());
        var searchNames = XiurenClient.SearchNames(job.Target, job.Aliases);
        var merged = new Dictionary<string, ResourceItem>(StringComparer.OrdinalIgnoreCase);

        ResourceItem? FindSaved(string detailUrl)
        {
            var saved = db.Resources.FirstOrDefault(x =>
                x.DetailUrl.Equals(detailUrl, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(x.PanUrl));
            if (saved != null && !saved.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            {
                saved.Model = model;
                db.Save();
            }
            return saved;
        }

        foreach (var searchName in searchNames)
        {
            if (job.MaxReady > 0 && merged.Count >= job.MaxReady) break;
            var remaining = job.MaxReady > 0 ? job.MaxReady - merged.Count : 0;
            Log($"搜索名称: {searchName} → 统一归档: {model}");
            var found = await new XiurenClient(settings).SearchAsync(
                searchName,
                Math.Max(1, job.Pages),
                remaining,
                Progress(),
                ct,
                FindSaved,
                item =>
                {
                    item.Model = model;
                    var stored = db.Upsert(item);
                    stored.Model = model;
                    db.Save();
                });

            foreach (var item in found)
            {
                item.Model = model;
                var stored = db.Upsert(item);
                stored.Model = model;
                merged[stored.DetailUrl] = stored;
            }
            db.Save();
            Log($"名称“{searchName}”合并后共有 {merged.Count} 条有效链接");
        }

        var modelItems = db.Resources
            .Where(x => x.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
            .Concat(merged.Values)
            .DistinctBy(x => x.DetailUrl, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var modelCategory = SiteCategoryClassifier.ResolveModelCategory(modelItems);
        foreach (var item in modelItems)
        {
            var detected = SiteCategoryClassifier.DetectedSpecialCategory(item);
            if (!string.IsNullOrWhiteSpace(detected))
                item.DetectedCategory = detected;
            item.Category = modelCategory;
        }
        Log($"模特统一分类: {model} → {modelCategory}（{merged.Count} 条）");
        db.Save();
        RefreshGrids();
        return merged.Values.ToList();
    }

    private void StopQueue()
    {
        stopRequested = true;
        cts?.Cancel();
    }

    private static string JobLabel(JobItem job) => job.Type switch
    {
        "Search" => "搜索入库 - " + SearchLabel(job),
        "SearchDownload" => "搜索并下载 - " + SearchLabel(job),
        "DownloadReady" => "下载就绪项",
        "DownloadModelReady" => "下载未完成 - " + job.Target,
        "ResumeIncomplete" => "续传本地未完成下载",
        _ => job.Type + " - " + job.Target
    };

    private static string SearchLabel(JobItem job)
    {
        var aliases = XiurenClient.SearchNames("", job.Aliases);
        return aliases.Count == 0 ? job.Target : $"{job.Target}（别名 {aliases.Count} 个）";
    }

    private async Task CheckVideosAsync()
    {
        if (videoScanRunning)
        {
            MessageBox.Show("视频完整性检查正在运行。");
            return;
        }
        if (cts != null)
        {
            MessageBox.Show("当前有下载任务正在运行，请等下载停止或完成后再检查。");
            return;
        }

        SaveSettings();
        if (string.IsNullOrWhiteSpace(settings.FfprobePath) || !File.Exists(settings.FfprobePath))
        {
            MessageBox.Show("找不到 ffprobe，请在设置页确认路径。");
            return;
        }

        var files = Directory.EnumerateFiles(settings.DownloadRoot, "*", SearchOption.AllDirectories)
            .Where(x => !AppPaths.IsInsideTool(x))
            .Where(x => settings.VideoExts.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0)
        {
            MessageBox.Show("下载目录中没有视频文件。");
            return;
        }

        videoScanRunning = true;
        Log($"开始检查视频完整性: {files.Length} 个，后台并发 8");
        try
        {
            var results = new ConcurrentBag<VideoValidationResult>();
            var completed = 0;
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = 8 },
                (file, token) =>
                {
                    results.Add(VideoValidator.Check(file, settings, sampleFrames: true, token));
                    var current = Interlocked.Increment(ref completed);
                    if (current % 50 == 0 || current == files.Length)
                        Log($"视频检查进度: {current}/{files.Length}");
                    return ValueTask.CompletedTask;
                });

            var all = results.ToArray();
            var setGroups = all
                .Select(x => new { Result = x, SetDir = TopLevelSetDir(x.Path) })
                .Where(x => !string.IsNullOrWhiteSpace(x.SetDir))
                .GroupBy(x => x.SetDir, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var group in setGroups)
            {
                VideoValidator.ClearInvalidMarker(group.Key);
                var invalid = group.Where(x => !x.Result.IsValid).Select(x => x.Result).ToArray();
                if (invalid.Length > 0)
                    VideoValidator.WriteInvalidMarker(group.Key, invalid);
            }

            var invalidByDir = setGroups
                .Select(x => new { Dir = x.Key, Invalid = x.Count(y => !y.Result.IsValid) })
                .Where(x => x.Invalid > 0)
                .ToDictionary(x => x.Dir, x => x.Invalid, StringComparer.OrdinalIgnoreCase);
            foreach (var item in db.Resources)
            {
                var dir = ResolveResourceLocalDir(item);
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (invalidByDir.TryGetValue(dir, out var invalidCount))
                {
                    item.Status = "Ready";
                    item.DownloadStatus = "Failed";
                    item.ExtractStatus = "";
                    item.Error = $"视频完整性检查失败: {invalidCount} 个文件，等待重新下载";
                }
                else if (item.Error.StartsWith("视频完整性检查失败:", StringComparison.Ordinal))
                {
                    item.DownloadStatus = "Downloaded";
                    item.ExtractStatus = "Extracted";
                    item.Error = "";
                }
            }

            var report = Path.Combine(AppPaths.DataDir, "video-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(report, JsonSerializer.Serialize(all, Settings.JsonOptions), Encoding.UTF8);
            db.Save();
            Scan();
            var bad = all.Count(x => !x.IsValid);
            var badSets = invalidByDir.Count;
            Log($"视频完整性检查完成: 正常 {all.Length - bad}，损坏 {bad}，涉及 {badSets} 套；报告: {report}");
            MessageBox.Show($"检查完成。\n正常: {all.Length - bad}\n损坏: {bad}\n涉及套图: {badSets}\n\n没有删除任何媒体文件。");
        }
        catch (Exception ex)
        {
            Log("视频完整性检查失败: " + ErrorText.Format(ex).Replace(Environment.NewLine, " | "));
            MessageBox.Show(ErrorText.Format(ex));
        }
        finally
        {
            videoScanRunning = false;
        }
    }

    private string TopLevelSetDir(string file)
    {
        var tracked = db.LocalFiles.FirstOrDefault(item =>
            Path.GetFullPath(file).StartsWith(
                Path.GetFullPath(item.LocalDir).TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase));
        if (tracked != null) return tracked.LocalDir;
        var relative = Path.GetRelativePath(settings.DownloadRoot, file);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 3) return Path.GetDirectoryName(file) ?? "";
        return Path.Combine(settings.DownloadRoot, parts[0], parts[1], parts[2]);
    }

    private string ResolveResourceLocalDir(ResourceItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.LocalDir)) return item.LocalDir;
        if (string.IsNullOrWhiteSpace(item.Model) || string.IsNullOrWhiteSpace(item.Title)) return "";
        return LibraryPaths.SetRoot(
            settings,
            item.Category,
            item.Model,
            item.Title);
    }

    private void Scan()
    {
        db.LocalFiles.Clear();
        foreach (var categoryDir in Directory.GetDirectories(settings.DownloadRoot)
                     .Where(x => !AppPaths.IsInsideTool(x)))
        {
            foreach (var modelDir in Directory.GetDirectories(categoryDir)
                         .Where(x => !AppPaths.IsInsideTool(x)))
            {
                foreach (var setDir in Directory.GetDirectories(modelDir)
                             .Where(x => !AppPaths.IsInsideTool(x)))
                {
                    var files = Directory.GetFiles(setDir, "*", SearchOption.AllDirectories)
                        .Where(x => !AppPaths.IsInsideTool(x))
                        .ToArray();
                    var videos = files.Where(x => settings.VideoExts.Contains(
                            Path.GetExtension(x),
                            StringComparer.OrdinalIgnoreCase))
                        .ToArray();
                    var quickInvalid = videos.Count(x =>
                        !VideoValidator.QuickHeaderLooksValid(x));
                    var invalidVideos = Math.Max(
                        quickInvalid,
                        VideoValidator.MarkedInvalidCount(setDir));
                    db.LocalFiles.Add(new LocalStat
                    {
                        Category = Path.GetFileName(categoryDir),
                        Model = Path.GetFileName(modelDir),
                        Title = Path.GetFileName(setDir),
                        LocalDir = setDir,
                        ImageCount = files.Count(IsImageForStats),
                        VideoCount = Math.Max(0, videos.Length - invalidVideos),
                        InvalidVideoCount = invalidVideos,
                        TotalBytes = files.Sum(f => new FileInfo(f).Length),
                        LastScanned = DateTime.Now.ToString("s")
                    });
                }
            }
        }
        db.Save();
        RefreshGrids();
        libraryView?.RefreshLibrary();
    }

    private void DeleteSelectedResources()
    {
        if (cts != null)
        {
            MessageBox.Show("当前有任务正在运行，请先停止或等待完成后再删除资源。");
            return;
        }

        var selected = resources.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as ResourceItem)
            .Where(x => x != null)
            .Cast<ResourceItem>()
            .Distinct()
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先在资源列表中选中要删除的行。");
            return;
        }

        if (MessageBox.Show($"确定从队列中删除选中的 {selected.Count} 条资源记录吗？\n不会删除已经下载到本地的图片或视频文件。", "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        foreach (var item in selected) db.Resources.Remove(item);
        db.Save();
        RefreshGrids();
        Log("已删除资源队列记录: " + selected.Count);
    }

    private void DeleteSelectedJobs()
    {
        var selected = jobs.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as JobItem)
            .Where(x => x != null)
            .Cast<JobItem>()
            .Distinct()
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("请先在任务队列中选中要删除的行。");
            return;
        }
        if (cts != null && selected.Any(IsRunning))
        {
            MessageBox.Show("当前任务正在运行，不能删除运行中的任务记录。");
            return;
        }

        if (MessageBox.Show($"确定删除选中的 {selected.Count} 条任务记录吗？", "确认删除", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        foreach (var item in selected) db.Jobs.Remove(item);
        db.Save();
        RefreshGrids();
        Log("已删除任务记录: " + selected.Count);
    }

    private void ClearFinishedJobs()
    {
        var removed = db.Jobs.RemoveAll(IsFinished);
        db.Save();
        RefreshGrids();
        Log("已清空已结束任务记录: " + removed);
    }

    private void ClearAllJobs()
    {
        if (cts != null)
        {
            MessageBox.Show("当前有任务正在运行，请先停止或等待完成后再清空全部任务。");
            return;
        }
        if (db.Jobs.Count == 0) return;
        if (MessageBox.Show($"确定清空全部 {db.Jobs.Count} 条任务记录吗？", "确认清空", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        var removed = db.Jobs.Count;
        db.Jobs.Clear();
        db.Save();
        RefreshGrids();
        Log("已清空全部任务记录: " + removed);
    }

    private static bool IsRunning(JobItem job) => job.Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
    private static bool IsFinished(JobItem job) => job.Status is "Done" or "Canceled" or "Failed";

    private void MarkStaleRunningJobs()
    {
        var changed = false;
        foreach (var job in db.Jobs.Where(IsRunning))
        {
            job.Status = "Canceled";
            job.Error = "程序上次关闭时任务仍在运行，已自动标记为已停止。";
            job.FinishedAt = DateTime.Now.ToString("s");
            changed = true;
        }
        if (changed) db.Save();
    }

    private void RefreshGrids()
    {
        refreshingGrids = true;
        resources.SuspendLayout();
        jobs.SuspendLayout();
        stats.SuspendLayout();
        try
        {
            resources.DataSource = new BindingList<ResourceItem>(db.Resources.OrderByDescending(x => x.LastChecked).ToList());
            jobs.DataSource = new BindingList<JobItem>(db.Jobs.ToList());
            stats.DataSource = new BindingList<ModelStat>(db.LocalFiles
                .GroupBy(x => new { x.Category, x.Model })
                .Select(g => new ModelStat
            {
                Category = g.Key.Category,
                Model = g.Key.Model,
                SetCount = g.Count(),
                ImageCount = g.Sum(x => x.ImageCount),
                VideoCount = g.Sum(x => x.VideoCount),
                InvalidVideoCount = g.Sum(x => x.InvalidVideoCount),
                TotalBytes = g.Sum(x => x.TotalBytes),
                FailedCount = db.Resources.Count(r =>
                    r.Category == g.Key.Category &&
                    r.Model == g.Key.Model &&
                    r.DownloadStatus == "Failed")
            }).OrderBy(x => x.Category).ThenBy(x => x.Model).ToList());
        }
        finally
        {
            refreshingGrids = false;
            resources.ResumeLayout();
            jobs.ResumeLayout();
            stats.ResumeLayout();
        }
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        if (refreshingGrids) return;
        details.DataSource = stats.CurrentRow?.DataBoundItem is ModelStat m
            ? new BindingList<LocalStat>(db.LocalFiles.Where(x =>
                x.Category == m.Category &&
                x.Model == m.Model).ToList())
            : new BindingList<LocalStat>();
    }

    private void OpenSelectedModelFolder()
    {
        if (stats.CurrentRow?.DataBoundItem is ModelStat m)
            OpenFolder(LibraryPaths.ModelRoot(settings, m.Category, m.Model));
    }

    private void OpenSelectedSetFolder()
    {
        if (details.CurrentRow?.DataBoundItem is LocalStat s)
            OpenFolder(s.LocalDir);
    }

    private void OpenSelectedSetInLibrary()
    {
        if (details.CurrentRow?.DataBoundItem is not LocalStat item) return;
        tabs.SelectedTab = libraryPage;
        libraryView.OpenSet(item);
    }

    private static void OpenFolder(string dir)
    {
        if (!Directory.Exists(dir))
        {
            MessageBox.Show("目录不存在: " + dir);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
    }

    private bool IsImageForStats(string f) =>
        MediaFileValidator.QuickImageHeaderLooksValid(f) &&
        settings.ImageExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase);

    private bool IsValidVideoForStats(string f)
    {
        if (!settings.VideoExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) return false;
        return VideoValidator.QuickHeaderLooksValid(f);
    }

    private void LoadSettingsToUi()
    {
        baseUrl.Text = settings.BaseUrl;
        user.Text = settings.UserName;
        pass.Text = settings.Password;
        category.Text = settings.CategoryPath;
        root.Text = settings.DownloadRoot;
        baidu.Text = settings.BaiduPcsPath;
        seven.Text = settings.SevenZipPath;
        ffprobe.Text = settings.FfprobePath;
        parallel.Value = Math.Clamp(settings.DownloadParallelism, 1, 5);
        singleFileParallel.Value = Math.Clamp(settings.SingleFileParallelism, 1, 20);
        delArchive.Checked = settings.DeleteArchiveAfterExtract;
        skipDone.Checked = settings.SkipCompleted;
        keepSidecar.Checked = settings.KeepSidecarFiles;
        useSystemProxy.Checked = settings.UseSystemProxy;
        mode.SelectedIndex = settings.SearchMode == "Category" ? 1 : 0;
        pages.Value = pages.Maximum;
        max.Value = max.Maximum;
    }

    private void SaveSettings()
    {
        settings.BaseUrl = NormalizeBaseUrl(baseUrl.Text);
        settings.UserName = user.Text.Trim();
        settings.Password = pass.Text;
        settings.CategoryPath = category.Text.Trim();
        settings.DownloadRoot = root.Text.Trim();
        settings.BaiduPcsPath = baidu.Text.Trim();
        settings.SevenZipPath = seven.Text.Trim();
        settings.FfprobePath = ffprobe.Text.Trim();
        settings.DownloadParallelism = (int)parallel.Value;
        settings.SingleFileParallelism = (int)singleFileParallel.Value;
        settings.DeleteArchiveAfterExtract = delArchive.Checked;
        settings.SkipCompleted = skipDone.Checked;
        settings.KeepSidecarFiles = keepSidecar.Checked;
        settings.UseSystemProxy = useSystemProxy.Checked;
        settings.SearchMode = mode.SelectedIndex == 1 ? "Category" : "Global";
        settings.Save();
    }

    private static string NormalizeBaseUrl(string value)
    {
        value = (value ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(value)) return "https://260704.xiurentua.cc";
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return value.TrimEnd('/');
    }

    private IProgress<string> Progress() => new Progress<string>(Log);
    private void Log(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        pendingLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {text.TrimEnd()}");
        if (pendingLogs.Count > 2000)
            while (pendingLogs.Count > 1000 && pendingLogs.TryDequeue(out _)) { }
    }

    private void FlushLogs()
    {
        if (log == null || pendingLogs.IsEmpty) return;
        var lines = new List<string>();
        while (lines.Count < 200 && pendingLogs.TryDequeue(out var line))
            lines.Add(line);
        if (lines.Count == 0) return;

        var text = string.Join("\r\n", lines) + "\r\n";
        if (log.TextLength > 250_000) log.Clear();
        log.AppendText(text);
        try
        {
            File.AppendAllText(
                LogMaintenance.CurrentLogPath("", settings.LogMaxFileMB),
                text,
                Encoding.UTF8);
            if (DateTime.UtcNow >= nextLogCleanup)
            {
                LogMaintenance.Cleanup(settings);
                nextLogCleanup = DateTime.UtcNow.AddHours(1);
            }
        }
        catch { }
    }

    private static Label Label(string text, int x, int y, int w) => new() { Text = text, Location = new Point(x, y), Size = new Size(w, 24) };
    private static TextBox TextBox(Control parent, int x, int y, int w, string text) { var box = new TextBox { Location = new Point(x, y), Width = w, Text = text }; parent.Controls.Add(box); return box; }
    private static NumericUpDown Number(Control parent, int x, int y, int min, int max, int value) { var n = new NumericUpDown { Location = new Point(x, y), Width = 70, Minimum = min, Maximum = max, Value = value }; parent.Controls.Add(n); return n; }
    private static Button Button(string text, int x, int y, int w, EventHandler handler) { var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 30) }; b.Click += handler; return b; }
    private static CheckBox CheckBox(Control parent, string text, int x, int y, int w) { var c = new CheckBox { Text = text, Location = new Point(x, y), Width = w }; parent.Controls.Add(c); return c; }
    private static DataGridView Grid() => new ResponsiveDataGridView()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AutoGenerateColumns = true,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true
    };
}
