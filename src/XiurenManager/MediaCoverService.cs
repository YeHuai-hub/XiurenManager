using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XiurenDownloader;

namespace XiurenManager;

internal static class MediaCoverService
{
    private static readonly SemaphoreSlim LoadGate = new(3, 3);
    private static readonly SemaphoreSlim ViewerPreviewGate = new(1, 1);
    private static readonly SemaphoreSlim ViewerUpgradeGate = new(1, 1);
    private static readonly SemaphoreSlim ViewerPreloadGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, WeakReference<ImageSource>> CoverCache = new();
    private static readonly SemaphoreSlim[] PersistentCoverGates = Enumerable.Range(0, 32)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private static readonly object ViewerCacheGate = new();
    private static readonly Dictionary<string, ViewerImageCacheEntry> ViewerImageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxViewerCacheItems = 8;
    private const long MaxViewerCacheBytes = 384L * 1024 * 1024;
    private const long MaxViewerCacheItemBytes = 192L * 1024 * 1024;
    public const int ViewerDisplayDecodeWidth = 4096;
    private const int ViewerPreloadDecodeWidth = 2048;
    private static long viewerCacheBytes;
    private static long viewerCacheSequence;

    private sealed class ViewerImageCacheEntry
    {
        public required string SourcePath { get; init; }
        public required string Signature { get; init; }
        public required BitmapSource Image { get; init; }
        public required long EstimatedBytes { get; init; }
        public required int DecodeWidth { get; init; }
        public long LastAccess { get; set; }
    }

    public static async Task<ImageSource?> LoadCoverAsync(
        LocalStat item,
        Settings settings,
        CancellationToken token,
        int decodeWidth = 440)
    {
        if (string.IsNullOrWhiteSpace(item.SetId)) return null;
        return await LoadPersistentCoverAsync(
            LibraryCatalogService.CoverPath(item.SetId),
            token,
            decodeWidth).ConfigureAwait(false);
    }

    public static async Task<ImageSource?> LoadPersistentCoverAsync(
        string path,
        CancellationToken token,
        int decodeWidth = 440)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var info = new FileInfo(path);
        var cacheKey = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + decodeWidth;
        if (CoverCache.TryGetValue(cacheKey, out var cached) &&
            cached.TryGetTarget(out var cachedImage))
        {
            return cachedImage;
        }

        await LoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (CoverCache.TryGetValue(cacheKey, out cached) &&
                cached.TryGetTarget(out cachedImage))
            {
                return cachedImage;
            }

            var result = await Task.Run(
                () => LoadBitmap(path, decodeWidth),
                token).ConfigureAwait(false);
            CoverCache[cacheKey] = new WeakReference<ImageSource>(result);
            TrimDeadCacheEntries();
            return result;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    public static Task<string[]> FindPreviewMediaAsync(
        LocalStat item,
        Settings settings,
        int count,
        CancellationToken token)
    {
        return Task.Run(() => LibraryCatalogService.ReadCachedMedia(item)
            .Take(Math.Max(0, count))
            .Select(file =>
            {
                token.ThrowIfCancellationRequested();
                return file.Path;
            })
            .ToArray(), token);
    }

    public static async Task<ImageSource?> LoadMediaPreviewAsync(
        string path,
        Settings settings,
        CancellationToken token,
        int decodeWidth)
    {
        await LoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var imageExts = settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (imageExts.Contains(Path.GetExtension(path)))
            {
                try
                {
                    return await Task.Run(
                        () => LoadBitmap(path, decodeWidth),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    var converted = await ConvertToCoverAsync(
                        path,
                        settings,
                        token,
                        decodeWidth).ConfigureAwait(false);
                    return await Task.Run(
                        () => LoadBitmap(converted, decodeWidth),
                        token).ConfigureAwait(false);
                }
            }

            var videoExts = settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!videoExts.Contains(Path.GetExtension(path)))
                return null;
            var frame = await ConvertToCoverAsync(
                path,
                settings,
                token,
                decodeWidth).ConfigureAwait(false);
            return await Task.Run(
                () => LoadBitmap(frame, decodeWidth),
                token).ConfigureAwait(false);
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static (string? Image, string? Video) FindFirstMedia(
        string directory,
        HashSet<string> imageExts,
        HashSet<string> videoExts,
        CancellationToken token)
    {
        string? firstVideo = null;
        var checkedFiles = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if ((checkedFiles++ & 31) == 0)
                token.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file);
            if (imageExts.Contains(extension) && MediaFileValidator.QuickImageHeaderLooksValid(file))
                return (file, firstVideo);
            if (firstVideo == null &&
                videoExts.Contains(extension) &&
                VideoValidator.QuickHeaderLooksValid(file))
                firstVideo = file;
        }
        return (null, firstVideo);
    }

    private static void TrimDeadCacheEntries()
    {
        if (CoverCache.Count < 2000) return;
        foreach (var entry in CoverCache)
        {
            if (!entry.Value.TryGetTarget(out _))
                CoverCache.TryRemove(entry.Key, out _);
        }
    }

    public static BitmapSource LoadFullImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static bool TryGetViewerImage(string path, out BitmapSource? image)
    {
        image = null;
        var signature = ViewerFileSignature(path);
        if (signature == null) return false;
        lock (ViewerCacheGate)
        {
            var entry = ViewerImageCache.Values
                .Where(candidate => candidate.Signature.Equals(
                    signature,
                    StringComparison.OrdinalIgnoreCase))
                .MaxBy(candidate => candidate.DecodeWidth);
            if (entry == null) return false;
            entry.LastAccess = ++viewerCacheSequence;
            image = entry.Image;
            return true;
        }
    }

    public static bool TryGetViewerImage(
        string path,
        int decodeWidth,
        out BitmapSource? image)
    {
        image = null;
        var key = ViewerCacheKey(path, decodeWidth);
        if (key == null) return false;
        lock (ViewerCacheGate)
        {
            if (!ViewerImageCache.TryGetValue(key, out var entry)) return false;
            entry.LastAccess = ++viewerCacheSequence;
            image = entry.Image;
            return true;
        }
    }

    public static async Task<BitmapSource> LoadViewerImageAsync(string path, Settings settings, CancellationToken token)
    {
        return await LoadViewerImageCoreAsync(
            path,
            settings,
            token,
            ViewerDisplayDecodeWidth,
            ViewerUpgradeGate).ConfigureAwait(false);
    }

    public static async Task<BitmapSource> LoadViewerPreviewAsync(
        string path,
        Settings settings,
        CancellationToken token)
    {
        return await LoadViewerImageCoreAsync(
            path,
            settings,
            token,
            ViewerPreloadDecodeWidth,
            ViewerPreviewGate).ConfigureAwait(false);
    }

    private static async Task<BitmapSource> LoadViewerImageCoreAsync(
        string path,
        Settings settings,
        CancellationToken token,
        int decodeWidth,
        SemaphoreSlim loadGate)
    {
        if (TryGetViewerImage(path, decodeWidth, out var cached) && cached != null)
            return cached;

        await loadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (TryGetViewerImage(path, decodeWidth, out cached) && cached != null)
                return cached;

            BitmapSource image;
            try
            {
                image = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var loaded = LoadDecodedImage(path, decodeWidth);
                    token.ThrowIfCancellationRequested();
                    return loaded;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                var converted = await ConvertViewerImageAsync(
                    path,
                    settings,
                    token,
                    decodeWidth).ConfigureAwait(false);
                image = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var loaded = LoadDecodedImage(converted, decodeWidth);
                    token.ThrowIfCancellationRequested();
                    return loaded;
                }, token).ConfigureAwait(false);
            }

            AddViewerImageToCache(path, image, decodeWidth);
            return image;
        }
        finally
        {
            loadGate.Release();
        }
    }

    public static async Task PreloadViewerImageAsync(
        string path,
        Settings settings,
        CancellationToken token)
    {
        if (TryGetViewerImage(path, out _)) return;
        _ = await LoadViewerImageCoreAsync(
            path,
            settings,
            token,
            ViewerPreloadDecodeWidth,
            ViewerPreloadGate).ConfigureAwait(false);
    }

    public static void TrimViewerImageCache()
    {
        lock (ViewerCacheGate)
        {
            TrimViewerImageCacheLocked(maxItems: 2, maxBytes: 64L * 1024 * 1024);
        }
    }

    private static string? ViewerFileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return null;
        }
    }

    private static string? ViewerCacheKey(string path, int decodeWidth)
    {
        var signature = ViewerFileSignature(path);
        return signature == null ? null : signature + "|" + decodeWidth;
    }

    private static BitmapSource LoadDecodedImage(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void AddViewerImageToCache(
        string path,
        BitmapSource image,
        int decodeWidth)
    {
        var signature = ViewerFileSignature(path);
        var key = ViewerCacheKey(path, decodeWidth);
        if (signature == null || key == null) return;
        var sourcePath = Path.GetFullPath(path);
        var bitsPerPixel = Math.Max(1, image.Format.BitsPerPixel);
        var estimatedBytes = Math.Max(
            1L,
            ((long)image.PixelWidth * bitsPerPixel + 7) / 8 * image.PixelHeight);
        if (estimatedBytes > MaxViewerCacheItemBytes) return;

        lock (ViewerCacheGate)
        {
            foreach (var stale in ViewerImageCache
                         .Where(entry => entry.Value.SourcePath.Equals(
                                             sourcePath,
                                             StringComparison.OrdinalIgnoreCase) &&
                                         !entry.Value.Signature.Equals(
                                             signature,
                                             StringComparison.OrdinalIgnoreCase))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                viewerCacheBytes -= ViewerImageCache[stale].EstimatedBytes;
                ViewerImageCache.Remove(stale);
            }

            if (ViewerImageCache.TryGetValue(key, out var existing))
            {
                existing.LastAccess = ++viewerCacheSequence;
                return;
            }

            while (ViewerImageCache.Count >= MaxViewerCacheItems ||
                   viewerCacheBytes + estimatedBytes > MaxViewerCacheBytes)
            {
                RemoveOldestViewerImageLocked();
            }

            ViewerImageCache[key] = new ViewerImageCacheEntry
            {
                SourcePath = sourcePath,
                Signature = signature,
                Image = image,
                EstimatedBytes = estimatedBytes,
                DecodeWidth = decodeWidth,
                LastAccess = ++viewerCacheSequence
            };
            viewerCacheBytes += estimatedBytes;
        }
    }

    private static void TrimViewerImageCacheLocked(int maxItems, long maxBytes)
    {
        while (ViewerImageCache.Count > maxItems || viewerCacheBytes > maxBytes)
            RemoveOldestViewerImageLocked();
    }

    private static void RemoveOldestViewerImageLocked()
    {
        if (ViewerImageCache.Count == 0) return;
        var oldest = ViewerImageCache.MinBy(entry => entry.Value.LastAccess);
        viewerCacheBytes -= oldest.Value.EstimatedBytes;
        ViewerImageCache.Remove(oldest.Key);
    }

    public static async Task CreatePersistentCoverAsync(
        string sourcePath,
        string outputPath,
        Settings settings,
        CancellationToken token,
        int decodeWidth = 440)
    {
        if (File.Exists(outputPath)) return;
        var creationGate = PersistentCoverGates[
            (StringComparer.OrdinalIgnoreCase.GetHashCode(outputPath) & int.MaxValue) %
            PersistentCoverGates.Length];
        await creationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(outputPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temp = outputPath + ".tmp";
            TryDelete(temp);
            try
            {
                var isImage = settings.ImageExts.Contains(
                    Path.GetExtension(sourcePath),
                    StringComparer.OrdinalIgnoreCase);
                if (isImage)
                {
                    try
                    {
                        await Task.Run(() =>
                        {
                            token.ThrowIfCancellationRequested();
                            var bitmap = LoadBitmap(sourcePath, decodeWidth);
                            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = new FileStream(
                                temp,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None);
                            encoder.Save(stream);
                        }, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        var converted = await ConvertToCoverAsync(
                            sourcePath,
                            settings,
                            token,
                            decodeWidth).ConfigureAwait(false);
                        File.Copy(converted, temp, true);
                    }
                }
                else
                {
                    var converted = await ConvertToCoverAsync(
                        sourcePath,
                        settings,
                        token,
                        decodeWidth).ConfigureAwait(false);
                    File.Copy(converted, temp, true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, outputPath, true);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        finally
        {
            creationGate.Release();
        }
    }

    private static BitmapSource LoadBitmap(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static async Task<string> ConvertToCoverAsync(
        string path,
        Settings settings,
        CancellationToken token,
        int decodeWidth)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(settings.FfprobePath) ?? "", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("找不到 FFmpeg", ffmpeg);

        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + decodeWidth)));
        var cacheDir = Path.Combine(AppPaths.DataDir, "cover-cache");
        Directory.CreateDirectory(cacheDir);
        var output = Path.Combine(cacheDir, hash + ".jpg");
        if (File.Exists(output)) return output;

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-ss");
        start.ArgumentList.Add("2");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-vf");
        var height = Math.Max(1, decodeWidth * 3 / 4);
        start.ArgumentList.Add(
            $"thumbnail,scale={decodeWidth}:{height}:force_original_aspect_ratio=increase," +
            $"crop={decodeWidth}:{height}");
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-q:v");
        start.ArgumentList.Add("3");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 FFmpeg");
        using var cancellation = token.Register(() => KillProcess(process));
        string error;
        try
        {
            error = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "无法生成封面" : error.Trim());
        return output;
    }

    private static async Task<string> ConvertViewerImageAsync(
        string path,
        Settings settings,
        CancellationToken token,
        int decodeWidth)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(settings.FfprobePath) ?? "", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("找不到 FFmpeg", ffmpeg);

        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks +
            "|viewer|" + decodeWidth)));
        var cacheDir = Path.Combine(AppPaths.DataDir, "image-cache");
        Directory.CreateDirectory(cacheDir);
        var output = Path.Combine(cacheDir, hash + ".png");
        if (File.Exists(output)) return output;

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-hide_banner");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add("-vf");
        start.ArgumentList.Add($"scale={decodeWidth}:-2:force_original_aspect_ratio=decrease");
        start.ArgumentList.Add("-frames:v");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add(output);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 FFmpeg");
        using var cancellation = token.Register(() => KillProcess(process));
        string error;
        try
        {
            error = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException(string.IsNullOrWhiteSpace(error) ? "无法解码图片" : error.Trim());
        return output;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
