namespace LinuxCloth.Runtime.Qemu.Qmp;

public enum QmpKeyCode
{
    Space,
}

public interface IQmpMonitor : IAsyncDisposable
{
    Task<string> QueryStatusAsync(CancellationToken cancellationToken = default);

    Task<QmpEvent> WaitForEventAsync(
        string eventName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task SystemPowerdownAsync(CancellationToken cancellationToken = default);

    Task SendKeyAsync(
        QmpKeyCode keyCode,
        CancellationToken cancellationToken = default);

    Task QuitAsync(CancellationToken cancellationToken = default);
}

public interface IQmpConnector
{
    Task<IQmpMonitor> ConnectAsync(
        string socketPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed class QmpConnector : IQmpConnector
{
    public async Task<IQmpMonitor> ConnectAsync(
        string socketPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        await QmpClient.ConnectAsync(socketPath, timeout, cancellationToken).ConfigureAwait(false);
}
