using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LinuxCloth.Runtime.Qemu.Qmp;

public sealed partial class QmpClient : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private long _nextCommandId;
    private bool _disposed;

    private QmpClient(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    public static async Task<QmpClient> ConnectAsync(
        string socketPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath))
        {
            throw new ArgumentException("The QMP socket path must be absolute.", nameof(socketPath));
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        Exception? lastError = null;
        while (!timeoutSource.IsCancellationRequested)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeoutSource.Token).ConfigureAwait(false);
                var client = new QmpClient(socket);
                await client.InitializeAsync(timeoutSource.Token).ConfigureAwait(false);
                return client;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                socket.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutSource.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new TimeoutException($"Timed out connecting to QMP socket '{socketPath}'.", lastError);
    }

    public async Task<string> QueryStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteAsync("query-status", cancellationToken).ConfigureAwait(false);
        if (!response.RootElement.TryGetProperty("return", out var result) ||
            !result.TryGetProperty("status", out var status))
        {
            throw new QmpException("QMP query-status response did not include a status.");
        }

        return status.GetString() ?? throw new QmpException("QMP returned a null VM status.");
    }

    public async Task SystemPowerdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteAsync("system_powerdown", cancellationToken).ConfigureAwait(false);
    }

    public async Task QuitAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteAsync("quit", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();
        _commandLock.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var greeting = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
        if (!greeting.RootElement.TryGetProperty("QMP", out _))
        {
            throw new QmpException("The server did not send a QMP greeting.");
        }

        using var response = await ExecuteAsync("qmp_capabilities", cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new QmpCommand(command, id), QmpJsonContext.Default.QmpCommand);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                var response = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
                if (response.RootElement.TryGetProperty("event", out _))
                {
                    response.Dispose();
                    continue;
                }

                if (!response.RootElement.TryGetProperty("id", out var responseId) ||
                    !string.Equals(responseId.GetString(), id, StringComparison.Ordinal))
                {
                    response.Dispose();
                    throw new QmpException("QMP response identifier did not match the command.");
                }

                if (response.RootElement.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("desc", out var description)
                        ? description.GetString()
                        : error.GetRawText();
                    response.Dispose();
                    throw new QmpException(message ?? "QMP command failed.");
                }

                return response;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            var count = await _stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The QMP socket closed unexpectedly.");
            }

            if (oneByte[0] == (byte)'\n')
            {
                break;
            }

            if (oneByte[0] != (byte)'\r')
            {
                buffer.Write(oneByte);
            }

            if (buffer.WrittenCount > 1024 * 1024)
            {
                throw new QmpException("A QMP message exceeded 1 MiB.");
            }
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private sealed record QmpCommand(
        [property: System.Text.Json.Serialization.JsonPropertyName("execute")] string Execute,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id);

    [System.Text.Json.Serialization.JsonSerializable(typeof(QmpCommand))]
    private sealed partial class QmpJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}

public sealed class QmpException : Exception
{
    public QmpException(string message)
        : base(message)
    {
    }
}
