namespace LinuxCloth.GuestBridge;

internal sealed class SingleInstanceGuard : IDisposable
{
    private const string SemaphoreName = @"Global\linuxcloth-guest-bridge-v1";

    private readonly Semaphore _semaphore;
    private bool _disposed;

    private SingleInstanceGuard(Semaphore semaphore)
    {
        _semaphore = semaphore;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        var semaphore = new Semaphore(
            initialCount: 1,
            maximumCount: 1,
            SemaphoreName);
        var acquired = false;
        try
        {
            acquired = semaphore.WaitOne(TimeSpan.Zero);
            return acquired ? new SingleInstanceGuard(semaphore) : null;
        }
        finally
        {
            if (!acquired)
            {
                semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _semaphore.Release();
        _semaphore.Dispose();
        _disposed = true;
    }
}
