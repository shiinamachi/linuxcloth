using System.Diagnostics;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public interface IImageBuildEndpointWaiter
{
    Task WaitAsync(
        string path,
        IManagedProcess owner,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class ImageBuildEndpointWaiter : IImageBuildEndpointWaiter
{
    public async Task WaitAsync(
        string path,
        IManagedProcess owner,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return;
            }

            if (owner.HasExited)
            {
                throw new WindowsImageBuildException(
                    $"Process {owner.Id} exited before creating its Unix socket.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for the image-builder Unix socket: {path}");
    }
}
