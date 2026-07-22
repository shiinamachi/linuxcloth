using System.Net.Sockets;
using System.Text;
using LinuxCloth.Runtime.Qemu.Qmp;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class QmpClientTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NegotiatesCapabilitiesAndQueriesStatus()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(_directory, "qmp.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var server = ServeAsync(listener);

        await using var client = await QmpClient.ConnectAsync(socketPath, TimeSpan.FromSeconds(2));

        await client.SendKeyAsync(QmpKeyCode.Space);
        Assert.Equal("running", await client.QueryStatusAsync());
        Assert.Equal("RESUME", (await client.WaitForEventAsync("RESUME", TimeSpan.FromSeconds(1))).Name);
        await server;
    }

    [Fact]
    public async Task QuitAcceptsEarlyEndOfStream()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(_directory, "quit.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var server = ServeQuitAsync(listener);

        await using var client = await QmpClient.ConnectAsync(socketPath, TimeSpan.FromSeconds(2));

        await client.QuitAsync();
        await server;
    }

    [Fact]
    public async Task MissingSocketReportsTimeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(_directory, "missing.sock");

        await Assert.ThrowsAsync<TimeoutException>(
            () => QmpClient.ConnectAsync(socketPath, TimeSpan.FromMilliseconds(150)));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task ServeAsync(Socket listener)
    {
        using var socket = await listener.AcceptAsync();
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await WriteAsync(stream, "{\"QMP\":{\"version\":{\"qemu\":{\"major\":11,\"minor\":0,\"micro\":0},\"package\":\"\"},\"capabilities\":[]}}");

        var capabilities = await ReadAsync(stream);
        Assert.Contains("qmp_capabilities", capabilities, StringComparison.Ordinal);
        await WriteAsync(stream, "{\"return\":{},\"id\":\"1\"}");

        var query = await ReadAsync(stream);
        using (var command = System.Text.Json.JsonDocument.Parse(query))
        {
            Assert.Equal("send-key", command.RootElement.GetProperty("execute").GetString());
            var key = command.RootElement.GetProperty("arguments").GetProperty("keys")[0];
            Assert.Equal("qcode", key.GetProperty("type").GetString());
            Assert.Equal("spc", key.GetProperty("data").GetString());
        }

        await WriteAsync(stream, "{\"return\":{},\"id\":\"2\"}");

        query = await ReadAsync(stream);
        Assert.Contains("query-status", query, StringComparison.Ordinal);
        await WriteAsync(stream, "{\"event\":\"RESUME\",\"data\":{}}");
        await WriteAsync(stream, "{\"return\":{\"status\":\"running\",\"running\":true},\"id\":\"3\"}");
    }

    private static async Task ServeQuitAsync(Socket listener)
    {
        using var socket = await listener.AcceptAsync();
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await WriteAsync(stream, "{\"QMP\":{\"version\":{\"qemu\":{\"major\":11,\"minor\":0,\"micro\":0},\"package\":\"\"},\"capabilities\":[]}}");
        _ = await ReadAsync(stream);
        await WriteAsync(stream, "{\"return\":{},\"id\":\"1\"}");
        var quit = await ReadAsync(stream);
        Assert.Contains("quit", quit, StringComparison.Ordinal);
    }

    private static async Task<string> ReadAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer) > 0 && buffer[0] != (byte)'\n')
        {
            if (buffer[0] != (byte)'\r')
            {
                bytes.Add(buffer[0]);
            }
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    private static async Task WriteAsync(Stream stream, string message)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(message + "\r\n"));
        await stream.FlushAsync();
    }
}
