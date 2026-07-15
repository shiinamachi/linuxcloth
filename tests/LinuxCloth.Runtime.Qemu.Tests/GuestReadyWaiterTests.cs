using System.Net.Sockets;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class GuestReadyWaiterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lc-ready-{Guid.NewGuid():N}"[..20]);

    [Fact]
    public async Task AcceptsOnlyTheExpectedSessionHandshake()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var socketPath = Path.Combine(_root, "ready.sock");
        var sessionId = Guid.NewGuid();
        using var listener = CreateListener(socketPath);
        var wait = new UnixGuestReadyWaiter().WaitAsync(
            socketPath,
            sessionId,
            TimeSpan.FromSeconds(5));

        using var accepted = await listener.AcceptAsync();
        _ = await accepted.SendAsync(GuestReadyProtocol.CreateMessage(sessionId));
        accepted.Shutdown(SocketShutdown.Send);

        await wait;
    }

    [Fact]
    public async Task RejectsAHandshakeFromAnotherSession()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var socketPath = Path.Combine(_root, "wrong.sock");
        using var listener = CreateListener(socketPath);
        var wait = new UnixGuestReadyWaiter().WaitAsync(
            socketPath,
            Guid.NewGuid(),
            TimeSpan.FromSeconds(5));

        using var accepted = await listener.AcceptAsync();
        _ = await accepted.SendAsync(GuestReadyProtocol.CreateMessage(Guid.NewGuid()));
        accepted.Shutdown(SocketShutdown.Send);

        await Assert.ThrowsAsync<InvalidDataException>(() => wait);
    }

    [Fact]
    public async Task PreservesCallerCancellation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var socketPath = Path.Combine(_root, "cancel.sock");
        using var listener = CreateListener(socketPath);
        using var cancellation = new CancellationTokenSource();
        var wait = new UnixGuestReadyWaiter().WaitAsync(
            socketPath,
            Guid.NewGuid(),
            TimeSpan.FromSeconds(5),
            cancellation.Token);

        using var accepted = await listener.AcceptAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static Socket CreateListener(string socketPath)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        return listener;
    }
}
