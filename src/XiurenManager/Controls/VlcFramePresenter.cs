using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace XiurenManager.Controls;

internal sealed class VlcFramePresenter : IDisposable
{
    private readonly Image target;
    private readonly Dispatcher dispatcher;
    private readonly object bufferGate = new();
    private readonly VlcMediaPlayer.LibVLCVideoLockCb lockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoUnlockCb unlockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoDisplayCb displayCallback;
    private readonly VlcMediaPlayer.LibVLCVideoFormatCb formatCallback;
    private readonly VlcMediaPlayer.LibVLCVideoCleanupCb cleanupCallback;
    private IntPtr frameBuffer;
    private WriteableBitmap? bitmap;
    private uint width;
    private uint height;
    private uint pitch;
    private int frameQueued;
    private int generation;
    private bool disposed;

    public VlcFramePresenter(Image target)
    {
        this.target = target;
        dispatcher = target.Dispatcher;
        lockCallback = LockVideo;
        unlockCallback = UnlockVideo;
        displayCallback = DisplayVideo;
        formatCallback = ConfigureVideo;
        cleanupCallback = CleanupVideo;
    }

    public void Attach(VlcMediaPlayer player)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        player.SetVideoFormatCallbacks(formatCallback, cleanupCallback);
    }

    public void ClearFrame()
    {
        if (dispatcher.CheckAccess())
        {
            target.Source = null;
            bitmap = null;
            return;
        }

        dispatcher.BeginInvoke(ClearFrame, DispatcherPriority.Render);
    }

    private uint ConfigureVideo(
        ref IntPtr opaque,
        IntPtr chroma,
        ref uint videoWidth,
        ref uint videoHeight,
        ref uint pitches,
        ref uint lines)
    {
        if (disposed || videoWidth == 0 || videoHeight == 0) return 0;

        var stride = Align(videoWidth * 4, 32);
        var lineCount = Align(videoHeight, 32);
        var byteCount = (long)stride * lineCount;
        if (byteCount <= 0 || byteCount > int.MaxValue) return 0;

        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        pitches = stride;
        lines = lineCount;

        int currentGeneration;
        lock (bufferGate)
        {
            ReleaseBuffer();
            frameBuffer = Marshal.AllocHGlobal((int)byteCount);
            width = videoWidth;
            height = videoHeight;
            pitch = stride;
            currentGeneration = ++generation;
        }

        dispatcher.BeginInvoke(() => CreateBitmap(currentGeneration), DispatcherPriority.Render);
        return 1;
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        Monitor.Enter(bufferGate);
        Marshal.WriteIntPtr(planes, frameBuffer);
        return frameBuffer;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        Monitor.Exit(bufferGate);
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (disposed || Interlocked.Exchange(ref frameQueued, 1) != 0) return;
        dispatcher.BeginInvoke(RenderLatestFrame, DispatcherPriority.Render);
    }

    private void RenderLatestFrame()
    {
        try
        {
            lock (bufferGate)
            {
                if (disposed || bitmap == null || frameBuffer == IntPtr.Zero) return;
                var bytes = checked((int)(pitch * height));
                bitmap.WritePixels(
                    new Int32Rect(0, 0, (int)width, (int)height),
                    frameBuffer,
                    bytes,
                    (int)pitch);
            }
        }
        finally
        {
            Interlocked.Exchange(ref frameQueued, 0);
        }
    }

    private void CreateBitmap(int expectedGeneration)
    {
        lock (bufferGate)
        {
            if (disposed || expectedGeneration != generation || width == 0 || height == 0) return;
            bitmap = new WriteableBitmap(
                (int)width,
                (int)height,
                96,
                96,
                PixelFormats.Bgr32,
                null);
            target.Source = bitmap;
        }
    }

    private void CleanupVideo(ref IntPtr opaque)
    {
        lock (bufferGate)
        {
            ReleaseBuffer();
            width = 0;
            height = 0;
            pitch = 0;
            generation++;
        }
        Interlocked.Exchange(ref frameQueued, 0);
        ClearFrame();
    }

    private void ReleaseBuffer()
    {
        if (frameBuffer == IntPtr.Zero) return;
        Marshal.FreeHGlobal(frameBuffer);
        frameBuffer = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (bufferGate)
        {
            ReleaseBuffer();
            generation++;
        }
        ClearFrame();
    }

    private static uint Align(uint value, uint alignment) =>
        (value + alignment - 1) / alignment * alignment;
}
