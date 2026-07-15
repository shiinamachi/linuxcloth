using System.Net.Sockets;
using LinuxCloth.Core;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public interface IGuestReadyWaiter
{
    Task WaitAsync(
        string socketPath,
        Guid expectedSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class UnixGuestReadyWaiter : IGuestReadyWaiter
{
    public async Task WaitAsync(
        string socketPath,
        Guid expectedSessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException("The guest-ready socket path must be absolute.", nameof(socketPath));
        }

        if (expectedSessionId == Guid.Empty)
        {
            throw new ArgumentException("The expected guest session identifier cannot be empty.", nameof(expectedSessionId));
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The guest-ready host channel requires Linux Unix sockets.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(Path.GetFullPath(socketPath)),
                    timeoutSource.Token)
                .ConfigureAwait(false);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            var message = await ReadMessageAsync(stream, timeoutSource.Token).ConfigureAwait(false);
            if (!GuestReadyProtocol.TryParse(message, out var actualSessionId) ||
                actualSessionId != expectedSessionId)
            {
                throw new InvalidDataException("The guest-ready message is invalid or belongs to another session.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the session-bound guest-ready message.");
        }
    }

    private static async Task<byte[]> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[GuestReadyProtocol.MaximumMessageBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var bytesRead = await stream
                .ReadAsync(buffer.AsMemory(length, 1), cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("The guest-ready channel closed before a complete message arrived.");
            }

            length += bytesRead;
            if (buffer[length - 1] == (byte)'\n')
            {
                return buffer[..length];
            }
        }

        throw new InvalidDataException("The guest-ready message exceeds its fixed size limit.");
    }
}
