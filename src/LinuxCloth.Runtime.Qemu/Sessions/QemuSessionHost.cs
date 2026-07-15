using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Qmp;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed record QemuSessionStartRequest(
    QemuLaunchConfiguration Configuration,
    SessionPaths Paths,
    string WindowTitle,
    IProgress<SessionState>? Progress = null);

public sealed record QemuShutdownPolicy(
    TimeSpan GuestPowerdownTimeout,
    TimeSpan QmpQuitTimeout,
    TimeSpan TerminateTimeout,
    TimeSpan AuxiliaryTerminateTimeout)
{
    public static QemuShutdownPolicy Default { get; } = new(
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2));
}

public sealed class QemuSessionHost
{
    private readonly IProcessLauncher _processLauncher;
    private readonly IQmpConnector _qmpConnector;
    private readonly QemuShutdownPolicy _shutdownPolicy;

    public QemuSessionHost(
        IProcessLauncher processLauncher,
        IQmpConnector qmpConnector,
        QemuShutdownPolicy? shutdownPolicy = null)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _qmpConnector = qmpConnector ?? throw new ArgumentNullException(nameof(qmpConnector));
        _shutdownPolicy = shutdownPolicy ?? QemuShutdownPolicy.Default;
    }

    public async Task<QemuRunningSession> StartAsync(
        QemuSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePaths(request);

        IManagedProcess? swtpm = null;
        IManagedProcess? passt = null;
        IManagedProcess? qemu = null;
        IManagedProcess? viewer = null;
        IQmpMonitor? qmp = null;

        try
        {
            request.Progress?.Report(SessionState.StartingNetwork);
            swtpm = await StartLoggedAsync(
                SidecarCommandBuilder.BuildSwtpm(request.Configuration.Toolchain, request.Paths),
                request.Paths,
                "swtpm",
                cancellationToken).ConfigureAwait(false);
            await WaitForEndpointAsync(
                request.Paths.SwtpmSocketPath,
                swtpm,
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (request.Configuration.Request.NetworkEnabled)
            {
                passt = await StartLoggedAsync(
                    SidecarCommandBuilder.BuildPasst(request.Configuration.Toolchain, request.Paths),
                    request.Paths,
                    "passt",
                    cancellationToken).ConfigureAwait(false);
                await WaitForEndpointAsync(
                    request.Paths.PasstSocketPath,
                    passt,
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false);
            }

            request.Progress?.Report(SessionState.StartingVm);
            qemu = await StartLoggedAsync(
                QemuCommandBuilder.Build(request.Configuration),
                request.Paths,
                "qemu",
                cancellationToken).ConfigureAwait(false);

            request.Progress?.Report(SessionState.WaitingForGuest);
            qmp = await _qmpConnector.ConnectAsync(
                request.Paths.QmpSocketPath,
                TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);
            var status = await qmp.QueryStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status is not ("running" or "paused" or "prelaunch"))
            {
                throw new InvalidOperationException($"QEMU entered unexpected state '{status}'.");
            }

            if (request.Configuration.Request.DisplayMode == DisplayMode.Spice)
            {
                viewer = await StartLoggedAsync(
                    SidecarCommandBuilder.BuildViewer(
                        request.Configuration.Toolchain,
                        request.Paths,
                        request.WindowTitle),
                    request.Paths,
                    "viewer",
                    cancellationToken).ConfigureAwait(false);
            }

            request.Progress?.Report(SessionState.Running);
            return new QemuRunningSession(
                request.Paths,
                qemu,
                swtpm,
                passt,
                viewer,
                qmp,
                _shutdownPolicy,
                request.Progress);
        }
        catch (Exception startFailure)
        {
            request.Progress?.Report(SessionState.Failed);
            var cleanupFailures = new List<Exception>();
            try
            {
                await CleanupFailedStartAsync(qmp, viewer, qemu, passt, swtpm).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            request.Progress?.Report(SessionState.Cleaning);
            try
            {
                SessionCleaner.Delete(request.Paths);
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            request.Progress?.Report(SessionState.Completed);
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, startFailure);
                throw new AggregateException("Session start and cleanup failed.", cleanupFailures);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startFailure).Throw();
            throw;
        }
    }

    private async Task<IManagedProcess> StartLoggedAsync(
        ProcessSpec spec,
        SessionPaths paths,
        string name,
        CancellationToken cancellationToken)
    {
        var loggedSpec = new ProcessSpec(
            spec.FileName,
            spec.Arguments,
            spec.WorkingDirectory,
            spec.Environment,
            Path.Combine(paths.SessionDirectory, $"{name}.stdout.log"),
            Path.Combine(paths.SessionDirectory, $"{name}.stderr.log"),
            spec.InheritEnvironment);
        return await _processLauncher.StartAsync(loggedSpec, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupFailedStartAsync(
        IQmpMonitor? qmp,
        params IManagedProcess?[] processes)
    {
        var failures = new List<Exception>();
        if (qmp is not null)
        {
            try
            {
                await qmp.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (var process in processes.Where(static process => process is not null))
        {
            try
            {
                await StopAuxiliaryAsync(process!).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more failed-start cleanup operations failed.", failures);
        }
    }

    private async Task StopAuxiliaryAsync(IManagedProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                if (!await WaitForExitAsync(process, _shutdownPolicy.AuxiliaryTerminateTimeout).ConfigureAwait(false))
                {
                    await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WaitForEndpointAsync(
        string path,
        IManagedProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"Process {process.Id} exited before creating '{path}'.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for '{path}'.");
    }

    internal static async Task<bool> WaitForExitAsync(IManagedProcess process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            _ = await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static void ValidatePaths(QemuSessionStartRequest request)
    {
        var configuration = request.Configuration;
        var paths = request.Paths;
        var pairs = new (string Actual, string Expected)[]
        {
            (configuration.SessionDirectory, paths.SessionDirectory),
            (configuration.OverlayPath, paths.OverlayPath),
            (configuration.OvmfVariablesPath, paths.OvmfVariablesPath),
            (configuration.SwtpmSocketPath, paths.SwtpmSocketPath),
            (configuration.QmpSocketPath, paths.QmpSocketPath),
            (configuration.SpiceSocketPath, paths.SpiceSocketPath),
            (configuration.GuestBridgeSocketPath, paths.GuestBridgeSocketPath),
            (configuration.ConfigDirectory, paths.ConfigDirectory),
        };

        if (pairs.Any(pair => !string.Equals(
                Path.GetFullPath(pair.Actual),
                Path.GetFullPath(pair.Expected),
                StringComparison.Ordinal)))
        {
            throw new ArgumentException("QEMU configuration paths do not match the owned session directory.", nameof(request));
        }

        if (configuration.Request.NetworkEnabled &&
            !string.Equals(
                Path.GetFullPath(configuration.PasstSocketPath!),
                Path.GetFullPath(paths.PasstSocketPath),
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The passt socket is outside the owned session directory.", nameof(request));
        }
    }
}

public sealed class QemuRunningSession : IAsyncDisposable
{
    private readonly SessionPaths _paths;
    private readonly IManagedProcess _qemu;
    private readonly IManagedProcess _swtpm;
    private readonly IManagedProcess? _passt;
    private readonly IManagedProcess? _viewer;
    private readonly IQmpMonitor _qmp;
    private readonly QemuShutdownPolicy _policy;
    private readonly IProgress<SessionState>? _progress;
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private bool _stopped;
    private bool _disposed;

    internal QemuRunningSession(
        SessionPaths paths,
        IManagedProcess qemu,
        IManagedProcess swtpm,
        IManagedProcess? passt,
        IManagedProcess? viewer,
        IQmpMonitor qmp,
        QemuShutdownPolicy policy,
        IProgress<SessionState>? progress)
    {
        _paths = paths;
        _qemu = qemu;
        _swtpm = swtpm;
        _passt = passt;
        _viewer = viewer;
        _qmp = qmp;
        _policy = policy;
        _progress = progress;
    }

    public Guid SessionId => _paths.SessionId;

    public int QemuProcessId => _qemu.Id;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _qemu.WaitForExitAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        await _stopGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _progress?.Report(SessionState.Stopping);
            var failures = new List<Exception>();
            await CaptureFailureAsync(StopQemuAsync, failures).ConfigureAwait(false);
            await CaptureFailureAsync(() => StopOwnedProcessAsync(_viewer), failures).ConfigureAwait(false);
            await CaptureFailureAsync(() => StopOwnedProcessAsync(_passt), failures).ConfigureAwait(false);
            await CaptureFailureAsync(() => StopOwnedProcessAsync(_swtpm), failures).ConfigureAwait(false);
            await CaptureFailureAsync(async () => await _qmp.DisposeAsync().ConfigureAwait(false), failures).ConfigureAwait(false);
            await CaptureFailureAsync(async () => await _qemu.DisposeAsync().ConfigureAwait(false), failures).ConfigureAwait(false);

            try
            {
                _progress?.Report(SessionState.Cleaning);
                SessionCleaner.Delete(_paths);
                _progress?.Report(SessionState.Completed);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("One or more session cleanup operations failed.", failures);
            }
        }
        finally
        {
            _stopGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _stopGate.Dispose();
    }

    private async Task StopQemuAsync()
    {
        if (_qemu.HasExited)
        {
            return;
        }

        try
        {
            await _qmp.SystemPowerdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (QmpException)
        {
            // Continue with escalating local process shutdown.
        }

        if (await QemuSessionHost.WaitForExitAsync(_qemu, _policy.GuestPowerdownTimeout).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _qmp.QuitAsync(CancellationToken.None)
                .WaitAsync(_policy.QmpQuitTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is QmpException or TimeoutException)
        {
            // Continue with SIGTERM.
        }

        if (await QemuSessionHost.WaitForExitAsync(_qemu, _policy.QmpQuitTimeout).ConfigureAwait(false))
        {
            return;
        }

        await _qemu.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        if (!await QemuSessionHost.WaitForExitAsync(_qemu, _policy.TerminateTimeout).ConfigureAwait(false))
        {
            await _qemu.KillAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task StopOwnedProcessAsync(IManagedProcess? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                if (!await QemuSessionHost.WaitForExitAsync(process, _policy.AuxiliaryTerminateTimeout).ConfigureAwait(false))
                {
                    await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task CaptureFailureAsync(Func<Task> operation, List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
