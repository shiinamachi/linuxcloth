using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed record RecoveryPolicy(
    TimeSpan QmpConnectTimeout,
    TimeSpan QmpCommandTimeout,
    TimeSpan GuestPowerdownTimeout,
    TimeSpan QmpQuitTimeout,
    TimeSpan TerminateTimeout,
    TimeSpan KillTimeout,
    int MaximumSessions = 256)
{
    public const int MaximumAllowedSessions = 4096;

    public static RecoveryPolicy Default { get; } = new(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QmpConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QmpCommandTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(GuestPowerdownTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QmpQuitTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TerminateTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KillTimeout, TimeSpan.Zero);
        if (MaximumSessions is <= 0 or > MaximumAllowedSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumSessions),
                $"MaximumSessions must be between 1 and {MaximumAllowedSessions}.");
        }
    }
}

public enum RecoveryDisposition
{
    Cleaned,
    PreservedInvalidRecord,
    PreservedIdentityMismatch,
    PreservedFailure,
}

public sealed record RecoveryResult(
    string SessionDirectory,
    Guid? SessionId,
    RecoveryDisposition Disposition,
    string? ProcessName = null,
    string? Detail = null,
    Exception? Failure = null)
{
    public bool IsCleaned => Disposition == RecoveryDisposition.Cleaned;
}

public interface IRecoverySessionCleaner
{
    Task DeleteAsync(SessionPaths paths, CancellationToken cancellationToken = default);
}

public sealed class RecoverySessionCleaner : IRecoverySessionCleaner
{
    public Task DeleteAsync(SessionPaths paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionCleaner.Delete(paths);
        return Task.CompletedTask;
    }
}

public sealed class RecoverySessionManager
{
    private static readonly string[] ProcessStopOrder =
    [
        SessionProcessNames.Qemu,
        SessionProcessNames.Viewer,
        SessionProcessNames.Passt,
        SessionProcessNames.Swtpm,
    ];

    private readonly SessionRecordStore _recordStore;
    private readonly IProcessIdentityController _processController;
    private readonly IQmpConnector? _qmpConnector;
    private readonly IRecoverySessionCleaner _cleaner;
    private readonly RecoveryPolicy _policy;

    public RecoverySessionManager(
        SessionRecordStore recordStore,
        IProcessIdentityController processController,
        IQmpConnector? qmpConnector = null,
        IRecoverySessionCleaner? cleaner = null,
        RecoveryPolicy? policy = null)
    {
        _recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _qmpConnector = qmpConnector;
        _cleaner = cleaner ?? new RecoverySessionCleaner();
        _policy = policy ?? RecoveryPolicy.Default;
        _policy.Validate();
    }

    public async Task<IReadOnlyList<RecoveryResult>> RecoverAllAsync(
        string runtimeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (!Path.IsPathFullyQualified(runtimeRoot))
        {
            throw new ArgumentException("The runtime root must be absolute.", nameof(runtimeRoot));
        }

        var fullRuntimeRoot = Path.GetFullPath(runtimeRoot);
        var runtimeRootInfo = new DirectoryInfo(fullRuntimeRoot);
        if (runtimeRootInfo.Exists && runtimeRootInfo.LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to recover sessions through a symbolic-link runtime root.");
        }

        var sessionsRoot = new DirectoryInfo(Path.Combine(fullRuntimeRoot, "sessions"));
        if (!sessionsRoot.Exists)
        {
            return [];
        }

        if (sessionsRoot.LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to recover sessions through a symbolic-link sessions root.");
        }

        var entries = sessionsRoot.EnumerateFileSystemInfos()
            .Take(_policy.MaximumSessions + 1)
            .ToArray();
        if (entries.Length > _policy.MaximumSessions)
        {
            throw new InvalidDataException(
                $"Refusing to recover more than {_policy.MaximumSessions} session entries at once.");
        }

        Array.Sort(entries, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

        var results = new List<RecoveryResult>(entries.Length);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is not DirectoryInfo directory || directory.LinkTarget is not null ||
                !Guid.TryParseExact(directory.Name, "N", out var sessionId) ||
                !string.Equals(directory.Name, sessionId.ToString("N"), StringComparison.Ordinal))
            {
                results.Add(new RecoveryResult(
                    entry.FullName,
                    null,
                    RecoveryDisposition.PreservedInvalidRecord,
                    Detail: "The session entry is not a canonical, non-symbolic-link session directory."));
                continue;
            }

            try
            {
                var paths = SessionPaths.Create(fullRuntimeRoot, sessionId);
                results.Add(await RecoverAsync(paths, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new RecoveryResult(
                    directory.FullName,
                    sessionId,
                    RecoveryDisposition.PreservedFailure,
                    Detail: "Session recovery could not be initialized.",
                    Failure: exception));
            }
        }

        return results;
    }

    public async Task<RecoveryResult> RecoverAsync(
        SessionPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionDirectory = new DirectoryInfo(paths.SessionDirectory);
        if (!sessionDirectory.Exists || sessionDirectory.LinkTarget is not null)
        {
            return new RecoveryResult(
                paths.SessionDirectory,
                paths.SessionId,
                RecoveryDisposition.PreservedInvalidRecord,
                Detail: "The session directory is missing or is a symbolic link.");
        }

        SessionRecord record;
        try
        {
            record = await _recordStore.ReadAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RecoveryResult(
                paths.SessionDirectory,
                paths.SessionId,
                RecoveryDisposition.PreservedInvalidRecord,
                Detail: "The session record could not be validated.",
                Failure: exception);
        }

        try
        {
            var preflight = await InspectAllAsync(record, requireStopped: false, cancellationToken).ConfigureAwait(false);
            if (preflight is not null)
            {
                return preflight with { SessionDirectory = paths.SessionDirectory, SessionId = paths.SessionId };
            }

            foreach (var processName in ProcessStopOrder)
            {
                if (!record.Processes.TryGetValue(processName, out var identity))
                {
                    continue;
                }

                var stopResult = processName == SessionProcessNames.Qemu
                    ? await StopQemuAsync(paths, identity, cancellationToken).ConfigureAwait(false)
                    : await StopProcessAsync(processName, identity, cancellationToken).ConfigureAwait(false);
                if (stopResult is not null)
                {
                    return stopResult with { SessionDirectory = paths.SessionDirectory, SessionId = paths.SessionId };
                }
            }

            var finalInspection = await InspectAllAsync(record, requireStopped: true, cancellationToken).ConfigureAwait(false);
            if (finalInspection is not null)
            {
                return finalInspection with { SessionDirectory = paths.SessionDirectory, SessionId = paths.SessionId };
            }

            try
            {
                await _cleaner.DeleteAsync(paths, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(paths.SessionDirectory))
                {
                    throw new IOException("The recovery cleaner returned without removing the session directory.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return new RecoveryResult(
                    paths.SessionDirectory,
                    paths.SessionId,
                    RecoveryDisposition.PreservedFailure,
                    Detail: "All owned processes stopped, but session artifact cleanup failed.",
                    Failure: exception);
            }

            return new RecoveryResult(
                paths.SessionDirectory,
                paths.SessionId,
                RecoveryDisposition.Cleaned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RecoveryResult(
                paths.SessionDirectory,
                paths.SessionId,
                RecoveryDisposition.PreservedFailure,
                Detail: "Session recovery failed before cleanup completed.",
                Failure: exception);
        }
    }

    private async Task<RecoveryResult?> InspectAllAsync(
        SessionRecord record,
        bool requireStopped,
        CancellationToken cancellationToken)
    {
        foreach (var processName in ProcessStopOrder)
        {
            if (!record.Processes.TryGetValue(processName, out var identity))
            {
                continue;
            }

            var status = await _processController.InspectAsync(identity, cancellationToken).ConfigureAwait(false);
            if (status == RecoveryProcessStatus.IdentityMismatch)
            {
                return IdentityMismatch(processName);
            }

            if (status == RecoveryProcessStatus.Running)
            {
                if (requireStopped)
                {
                    return new RecoveryResult(
                        string.Empty,
                        null,
                        RecoveryDisposition.PreservedFailure,
                        processName,
                        $"Verified process '{processName}' is still running after recovery.");
                }

                continue;
            }
        }

        return null;
    }

    private async Task<RecoveryResult?> StopQemuAsync(
        SessionPaths paths,
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        var status = await _processController.InspectAsync(identity, cancellationToken).ConfigureAwait(false);
        if (status == RecoveryProcessStatus.IdentityMismatch)
        {
            return IdentityMismatch(SessionProcessNames.Qemu);
        }

        if (status == RecoveryProcessStatus.Stopped)
        {
            return null;
        }

        if (_qmpConnector is not null)
        {
            IQmpMonitor? monitor = null;
            try
            {
                monitor = await _qmpConnector.ConnectAsync(
                    paths.QmpSocketPath,
                    _policy.QmpConnectTimeout,
                    cancellationToken).ConfigureAwait(false);
                await monitor.SystemPowerdownAsync(cancellationToken)
                    .WaitAsync(_policy.QmpCommandTimeout, cancellationToken).ConfigureAwait(false);

                status = await _processController.WaitForExitAsync(
                    identity,
                    _policy.GuestPowerdownTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (status == RecoveryProcessStatus.IdentityMismatch)
                {
                    return IdentityMismatch(SessionProcessNames.Qemu);
                }

                if (status == RecoveryProcessStatus.Stopped)
                {
                    return null;
                }

                await monitor.QuitAsync(cancellationToken)
                    .WaitAsync(_policy.QmpCommandTimeout, cancellationToken).ConfigureAwait(false);
                status = await _processController.WaitForExitAsync(
                    identity,
                    _policy.QmpQuitTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (status == RecoveryProcessStatus.IdentityMismatch)
                {
                    return IdentityMismatch(SessionProcessNames.Qemu);
                }

                if (status == RecoveryProcessStatus.Stopped)
                {
                    return null;
                }
            }
            catch (Exception exception) when (
                exception is QmpException or TimeoutException or IOException or InvalidOperationException)
            {
                // A stale or failed QMP endpoint falls through to local signaling.
            }
            finally
            {
                if (monitor is not null)
                {
                    await monitor.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        return await StopProcessAsync(SessionProcessNames.Qemu, identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryResult?> StopProcessAsync(
        string processName,
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        var signal = await _processController.SendTerminateAsync(identity, cancellationToken).ConfigureAwait(false);
        if (signal == RecoverySignalResult.IdentityMismatch)
        {
            return IdentityMismatch(processName);
        }

        if (signal == RecoverySignalResult.Stopped)
        {
            return null;
        }

        var status = await _processController.WaitForExitAsync(
            identity,
            _policy.TerminateTimeout,
            cancellationToken).ConfigureAwait(false);
        if (status == RecoveryProcessStatus.IdentityMismatch)
        {
            return IdentityMismatch(processName);
        }

        if (status == RecoveryProcessStatus.Stopped)
        {
            return null;
        }

        signal = await _processController.SendKillAsync(identity, cancellationToken).ConfigureAwait(false);
        if (signal == RecoverySignalResult.IdentityMismatch)
        {
            return IdentityMismatch(processName);
        }

        if (signal == RecoverySignalResult.Stopped)
        {
            return null;
        }

        status = await _processController.WaitForExitAsync(
            identity,
            _policy.KillTimeout,
            cancellationToken).ConfigureAwait(false);
        return status switch
        {
            RecoveryProcessStatus.Stopped => null,
            RecoveryProcessStatus.IdentityMismatch => IdentityMismatch(processName),
            _ => new RecoveryResult(
                string.Empty,
                null,
                RecoveryDisposition.PreservedFailure,
                processName,
                $"Verified process '{processName}' did not stop after SIGKILL."),
        };
    }

    private static RecoveryResult IdentityMismatch(string processName) =>
        new(
            string.Empty,
            null,
            RecoveryDisposition.PreservedIdentityMismatch,
            processName,
            $"PID identity for '{processName}' no longer matches the persisted identity; no signal was sent.");
}
