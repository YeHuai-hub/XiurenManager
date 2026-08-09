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
    private static readonly SemaphoreSlim ViewerLoadGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, WeakReference<ImageSource>> CoverCache = new();

    public static async Task<ImageSource?> LoadCoverAsync(
        LocalStat item,
        Settings settings,
        CancellationToken token,
        int decodeWidth = 440)
    {
        var cacheKey = item.LocalDir + "|" + item.LastScanned + "|" + item.TotalBytes + "|" + decodeWidth;
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

            var imageExts = settings.ImageExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var videoExts = settings.VideoExts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var media = await Task.Run(
                () => FindFirstMedia(item.LocalDir, imageExts, videoExts, token),
                token).ConfigureAwait(false);
            ImageSource? result = null;
            var image = media.Image;
            if (image != null)
            {
                try
                {
                    result = await Task.Run(
                        () => LoadBitmap(image, decodeWidth),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    var converted = await ConvertToCoverAsync(
                        image,
                        settings,
                        token,
                        decodeWidth).ConfigureAwait(false);
                    result = await Task.Run(
                        () => LoadBitmap(converted, decodeWidth),
                        token).ConfigureAwait(false);
                }
            }
            else if (media.Video != null)
            {
                var converted = await ConvertToCoverAsync(
                    media.Video,
                    settings,
                    token,
                    decodeWidth).ConfigureAwait(false);
                result = await Task.Run(
                    () => LoadBitmap(converted, decodeWidth),
                    token).ConfigureAwait(false);
            }

            if (result != null)
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
        return Task.Run(() =>
        {
            var extensions = settings.ImageExts
                .Concat(settings.VideoExts)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(item.LocalDir, "*", SearchOption.AllDirectories)
                .Where(path => !AppPaths.IsInsideTool(path))
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Where(path => MediaFileValidator.IsUsable(path, settings.ImageExts, settings.VideoExts))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, count))
                .Select(path =>
                {
                    token.ThrowIfCancellationRequested();
                    return path;
                })
                .ToArray();
        }, token);
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

    public static async Task<BitmapSource> LoadViewerImageAsync(string path, Settings settings, CancellationToken token)
    {
        await ViewerLoadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try
            {
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var image = LoadFullImage(path);
                    token.ThrowIfCancellationRequested();
                    return image;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                var converted = await ConvertViewerImageAsync(path, settings, token).ConfigureAwait(false);
                return await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var image = LoadFullImage(converted);
                    token.ThrowIfCancellationRequested();
                    return image;
                }, token).ConfigureAwait(false);
            }
        }
        finally
        {
            ViewerLoadGate.Release();
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

    private static async Task<string> ConvertViewerImageAsync(string path, Settings settings, CancellationToken token)
    {
        var ffmpeg = Path.Combine(Path.GetDirectoryName(settings.FfprobePath) ?? "", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("找不到 FFmpeg", ffmpeg);

        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|viewer")));
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
        start.ArgumentList.Add("scale=8192:-2:force_original_aspect_ratio=decrease");
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
