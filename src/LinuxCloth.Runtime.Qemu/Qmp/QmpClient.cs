using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace LinuxCloth.Runtime.Qemu.Qmp;

public sealed partial class QmpClient : IQmpMonitor
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new(StringComparer.Ordinal);
    private readonly Channel<QmpEvent> _events = Channel.CreateUnbounded<QmpEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readerTask;
    private long _nextCommandId;
    private bool _quitRequested;
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

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Exception? lastError = null;

        try
        {
            while (true)
            {
                timeoutSource.Token.ThrowIfCancellationRequested();
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        timeoutSource.Token).ConfigureAwait(false);
                    var client = new QmpClient(socket);
                    try
                    {
                        await client.InitializeAsync(timeoutSource.Token).ConfigureAwait(false);
                        return client;
                    }
                    catch
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                catch (SocketException exception)
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
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out connecting to QMP socket '{socketPath}'.", lastError ?? exception);
        }
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

    public async Task<QmpEvent> WaitForEventAsync(
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (await _events.Reader.WaitToReadAsync(timeoutSource.Token).ConfigureAwait(false))
            {
                while (_events.Reader.TryRead(out var qmpEvent))
                {
                    if (string.Equals(qmpEvent.Name, eventName, StringComparison.Ordinal))
                    {
                        return qmpEvent;
                    }
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for QMP event '{eventName}'.", exception);
        }

        throw new QmpDisconnectedException("The QMP event stream ended.");
    }

    public async Task SystemPowerdownAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteAsync("system_powerdown", cancellationToken).ConfigureAwait(false);
    }

    public async Task SendKeyAsync(
        QmpKeyCode keyCode,
        CancellationToken cancellationToken = default)
    {
        var qcode = keyCode switch
        {
            QmpKeyCode.Space => "spc",
            _ => throw new ArgumentOutOfRangeException(nameof(keyCode)),
        };
        using var response = await ExecuteAsync(
                "send-key",
                new QmpSendKeyArguments([new QmpKeyValue("qcode", qcode)]),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task QuitAsync(CancellationToken cancellationToken = default)
    {
        _quitRequested = true;
        try
        {
            using var response = await ExecuteAsync("quit", cancellationToken).ConfigureAwait(false);
        }
        catch (QmpDisconnectedException) when (_quitRequested)
        {
            // QEMU is allowed to close QMP before sending the quit response.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
                // Disposal intentionally breaks a pending socket read.
            }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var greeting = await ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
        if (!greeting.RootElement.TryGetProperty("QMP", out _))
        {
            throw new QmpException("The server did not send a QMP greeting.");
        }

        _readerTask = ReadLoopAsync(_shutdown.Token);
        using var response = await ExecuteAsync("qmp_capabilities", cancellationToken).ConfigureAwait(false);
    }

    private Task<JsonDocument> ExecuteAsync(
        string command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(command, null, cancellationToken);

    private async Task<JsonDocument> ExecuteAsync(
        string command,
        QmpSendKeyArguments? arguments,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextCommandId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("A duplicate QMP command identifier was generated.");
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new QmpCommand(command, arguments, id),
                QmpJsonContext.Default.QmpCommand);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync("\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (message.RootElement.TryGetProperty("event", out var eventName))
                {
                    var data = message.RootElement.TryGetProperty("data", out var eventData)
                        ? eventData.Clone()
                        : default;
                    await _events.Writer.WriteAsync(
                        new QmpEvent(eventName.GetString() ?? string.Empty, data),
                        cancellationToken).ConfigureAwait(false);
                    message.Dispose();
                    continue;
                }

                if (!message.RootElement.TryGetProperty("id", out var responseId) ||
                    responseId.ValueKind != JsonValueKind.String ||
                    !_pending.TryGetValue(responseId.GetString()!, out var completion))
                {
                    message.Dispose();
                    continue;
                }

                if (message.RootElement.TryGetProperty("error", out var error))
                {
                    var description = error.TryGetProperty("desc", out var detail)
                        ? detail.GetString()
                        : error.GetRawText();
                    message.Dispose();
                    completion.TrySetException(new QmpException(description ?? "QMP command failed."));
                }
                else if (!completion.TrySetResult(message))
                {
                    message.Dispose();
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
        {
            failure = exception;
        }
        finally
        {
            var disconnected = new QmpDisconnectedException("The QMP connection closed.", failure);
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(disconnected);
            }

            _events.Writer.TryComplete(failure);
        }
    }

    private static async Task<JsonDocument> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
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
        [property: System.Text.Json.Serialization.JsonPropertyName("arguments")]
        [property: System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        QmpSendKeyArguments? Arguments,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id);

    private sealed record QmpSendKeyArguments(
        [property: System.Text.Json.Serialization.JsonPropertyName("keys")]
        QmpKeyValue[] Keys);

    private sealed record QmpKeyValue(
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
        [property: System.Text.Json.Serialization.JsonPropertyName("data")] string Data);

    [System.Text.Json.Serialization.JsonSerializable(typeof(QmpCommand))]
    private sealed partial class QmpJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}

public sealed record QmpEvent(string Name, JsonElement Data);

public class QmpException : Exception
{
    public QmpException(string message)
        : base(message)
    {
    }

    public QmpException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class QmpDisconnectedException : QmpException
{
    public QmpDisconnectedException(string message)
        : base(message)
    {
    }

    public QmpDisconnectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
