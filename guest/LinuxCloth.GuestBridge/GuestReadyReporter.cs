using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge;

internal interface IGuestReadyReporter
{
    Task ReportAsync(Guid sessionId, CancellationToken cancellationToken);
}

internal sealed class VirtioSerialGuestReadyReporter : IGuestReadyReporter
{
    internal const string DevicePath = @"\\.\org.linuxcloth.guestbridge.0";

    private readonly Func<Stream> _streamFactory;

    public VirtioSerialGuestReadyReporter()
        : this(OpenDevice)
    {
    }

    internal VirtioSerialGuestReadyReporter(Func<Stream> streamFactory)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
    }

    public async Task ReportAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = GuestReadyProtocol.CreateMessage(sessionId);
        await using var stream = _streamFactory();
        if (!stream.CanWrite)
        {
            throw new IOException("The virtio-serial guest-ready device is not writable.");
        }

        await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Stream OpenDevice()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The virtio-serial guest-ready device requires Windows.");
        }

        return new FileStream(
            DevicePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true);
    }
}

internal sealed class NullGuestReadyReporter : IGuestReadyReporter
{
    public static NullGuestReadyReporter Instance { get; } = new();

    private NullGuestReadyReporter()
    {
    }

    public Task ReportAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _ = sessionId;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
