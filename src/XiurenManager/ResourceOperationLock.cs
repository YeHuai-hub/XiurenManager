using XiurenDownloader;

namespace XiurenManager;

internal static class ResourceOperationLock
{
    private static string LockPath =>
        Path.Combine(AppPaths.DataDir, "resource-operation.lock");

    public static async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var lease = TryAcquire();
            if (lease != null)
                return lease;
            await Task.Delay(250, token);
        }
    }

    public static IDisposable? TryAcquire()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        try
        {
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}

internal static class ApplicationInstanceLock
{
    private static string LockPath =>
        Path.Combine(AppPaths.DataDir, "application-instance.lock");

    public static IDisposable? TryAcquire()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        try
        {
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
