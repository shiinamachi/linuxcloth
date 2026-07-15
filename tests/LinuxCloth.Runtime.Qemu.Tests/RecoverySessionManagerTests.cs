using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class RecoverySessionManagerTests : IDisposable
{
    private const string BootId = "11111111-2222-3333-4444-555555555555";
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), $"lcr-{Guid.NewGuid():N}"[..16]);
    private readonly SessionRecordStore _store = new();

    [Fact]
    public async Task PreservesPidReuseWithoutSendingSignals()
    {
        var (paths, identity) = await CreateSessionAsync();
        var processes = new FakeProcessIdentityController(identity, RecoveryProcessStatus.IdentityMismatch);
        var cleaner = new RecordingCleaner();
        var qmp = new FakeQmpConnector(processes);
        var manager = new RecoverySessionManager(_store, processes, qmp, cleaner, FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.PreservedIdentityMismatch, result.Disposition);
        Assert.Equal(SessionProcessNames.Qemu, result.ProcessName);
        Assert.Equal(0, processes.TerminateCount);
        Assert.Equal(0, processes.KillCount);
        Assert.Equal(0, qmp.ConnectCount);
        Assert.Equal(0, cleaner.DeleteCount);
        Assert.True(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task UsesQmpPowerdownThenCleansGracefullyStoppedSession()
    {
        var (paths, identity) = await CreateSessionAsync();
        var processes = new FakeProcessIdentityController(identity, RecoveryProcessStatus.Running);
        var qmp = new FakeQmpConnector(processes) { ExitOnPowerdown = true };
        var manager = new RecoverySessionManager(_store, processes, qmp, policy: FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.Cleaned, result.Disposition);
        Assert.Equal(1, qmp.ConnectCount);
        Assert.Equal(1, qmp.Monitor.PowerdownCount);
        Assert.Equal(0, qmp.Monitor.QuitCount);
        Assert.Equal(0, processes.TerminateCount);
        Assert.Equal(0, processes.KillCount);
        Assert.False(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task CleansStoppedFailedStateSession()
    {
        var (paths, identity) = await CreateSessionAsync(SessionState.Failed);
        var processes = new FakeProcessIdentityController(identity, RecoveryProcessStatus.Stopped);
        var manager = new RecoverySessionManager(_store, processes, policy: FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.Cleaned, result.Disposition);
        Assert.False(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task PreservesSessionWhenCleanupFails()
    {
        var (paths, identity) = await CreateSessionAsync();
        var processes = new FakeProcessIdentityController(identity, RecoveryProcessStatus.Stopped);
        var cleaner = new RecordingCleaner { Failure = new IOException("disk busy") };
        var manager = new RecoverySessionManager(_store, processes, cleaner: cleaner, policy: FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.PreservedFailure, result.Disposition);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(1, cleaner.DeleteCount);
        Assert.True(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task PreservesInvalidRecordWithoutInspectingProcesses()
    {
        var paths = SessionPaths.Create(_runtimeRoot, Guid.NewGuid());
        paths.CreateDirectories();
        await File.WriteAllTextAsync(paths.SessionRecordPath, "{\"schemaVersion\":1,\"unknown\":true}");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                paths.SessionRecordPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var processes = new FakeProcessIdentityController(
            new ProcessIdentity(101, BootId, 202, "/usr/bin/qemu-system-x86_64"),
            RecoveryProcessStatus.Stopped);
        var cleaner = new RecordingCleaner();
        var manager = new RecoverySessionManager(_store, processes, cleaner: cleaner, policy: FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.PreservedInvalidRecord, result.Disposition);
        Assert.Equal(0, processes.InspectCount);
        Assert.Equal(0, cleaner.DeleteCount);
        Assert.True(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task BoundsSessionEnumerationBeforeRecovery()
    {
        var sessionsRoot = Path.Combine(_runtimeRoot, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        Directory.CreateDirectory(Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(sessionsRoot, Guid.NewGuid().ToString("N")));
        var processes = new FakeProcessIdentityController(
            new ProcessIdentity(101, BootId, 202, "/usr/bin/qemu-system-x86_64"),
            RecoveryProcessStatus.Stopped);
        var policy = FastPolicy() with { MaximumSessions = 1 };
        var manager = new RecoverySessionManager(_store, processes, policy: policy);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.RecoverAllAsync(_runtimeRoot));

        Assert.Contains("more than 1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, processes.InspectCount);
    }

    [Fact]
    public async Task PreservesInterruptedStartupWithoutDurableQemuIdentity()
    {
        var paths = SessionPaths.Create(_runtimeRoot, Guid.NewGuid());
        paths.CreateDirectories();
        var record = new SessionRecord(
            paths.SessionId,
            BootId,
            SessionState.StartingNetwork,
            "test-image",
            new string('a', 64),
            [ServiceId.Parse("WooriBank")]);
        await _store.WriteAsync(paths, record);
        var processes = new FakeProcessIdentityController(
            new ProcessIdentity(101, BootId, 202, "/usr/bin/qemu-system-x86_64"),
            RecoveryProcessStatus.Stopped);
        var cleaner = new RecordingCleaner();
        var manager = new RecoverySessionManager(_store, processes, cleaner: cleaner, policy: FastPolicy());

        var result = await manager.RecoverAsync(paths);

        Assert.Equal(RecoveryDisposition.PreservedFailure, result.Disposition);
        Assert.Equal(0, processes.InspectCount);
        Assert.Equal(0, cleaner.DeleteCount);
        Assert.True(Directory.Exists(paths.SessionDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private async Task<(SessionPaths Paths, ProcessIdentity Identity)> CreateSessionAsync(
        SessionState state = SessionState.Running)
    {
        var paths = SessionPaths.Create(_runtimeRoot, Guid.NewGuid());
        paths.CreateDirectories();
        await File.WriteAllTextAsync(paths.OverlayPath, "overlay");
        var identity = new ProcessIdentity(101, BootId, 202, "/usr/bin/qemu-system-x86_64");
        var record = new SessionRecord(
            paths.SessionId,
            BootId,
            state,
            "test-image",
            new string('a', 64),
            [ServiceId.Parse("WooriBank")],
            new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal)
            {
                [SessionProcessNames.Qemu] = identity,
            });
        await _store.WriteAsync(paths, record);
        return (paths, identity);
    }

    private static RecoveryPolicy FastPolicy() =>
        new(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

    private sealed class FakeProcessIdentityController(
        ProcessIdentity expectedIdentity,
        RecoveryProcessStatus status) : IProcessIdentityController
    {
        public RecoveryProcessStatus Status { get; set; } = status;

        public int InspectCount { get; private set; }

        public int TerminateCount { get; private set; }

        public int KillCount { get; private set; }

        public Task<RecoveryProcessStatus> InspectAsync(
            ProcessIdentity expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedIdentity, expected);
            InspectCount++;
            return Task.FromResult(Status);
        }

        public Task<RecoverySignalResult> SendTerminateAsync(
            ProcessIdentity expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedIdentity, expected);
            TerminateCount++;
            return Task.FromResult(ToSignalResult());
        }

        public Task<RecoverySignalResult> SendKillAsync(
            ProcessIdentity expected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedIdentity, expected);
            KillCount++;
            return Task.FromResult(ToSignalResult());
        }

        public Task<RecoveryProcessStatus> WaitForExitAsync(
            ProcessIdentity expected,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedIdentity, expected);
            Assert.True(timeout > TimeSpan.Zero);
            return Task.FromResult(Status);
        }

        private RecoverySignalResult ToSignalResult() => Status switch
        {
            RecoveryProcessStatus.Running => RecoverySignalResult.Sent,
            RecoveryProcessStatus.Stopped => RecoverySignalResult.Stopped,
            RecoveryProcessStatus.IdentityMismatch => RecoverySignalResult.IdentityMismatch,
            _ => throw new InvalidOperationException("Unexpected fake process status."),
        };
    }

    private sealed class RecordingCleaner : IRecoverySessionCleaner
    {
        public Exception? Failure { get; init; }

        public int DeleteCount { get; private set; }

        public Task DeleteAsync(SessionPaths paths, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            Directory.Delete(paths.SessionDirectory, recursive: true);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeQmpConnector(FakeProcessIdentityController processes) : IQmpConnector
    {
        public bool ExitOnPowerdown
        {
            get => Monitor.ExitOnPowerdown;
            init => Monitor.ExitOnPowerdown = value;
        }

        public int ConnectCount { get; private set; }

        public FakeQmpMonitor Monitor { get; } = new(processes);

        public Task<IQmpMonitor> ConnectAsync(
            string socketPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Path.IsPathFullyQualified(socketPath));
            Assert.True(timeout > TimeSpan.Zero);
            ConnectCount++;
            return Task.FromResult<IQmpMonitor>(Monitor);
        }
    }

    private sealed class FakeQmpMonitor(FakeProcessIdentityController processes) : IQmpMonitor
    {
        public bool ExitOnPowerdown { get; set; }

        public int PowerdownCount { get; private set; }

        public int QuitCount { get; private set; }

        public Task<string> QueryStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("running");

        public Task<QmpEvent> WaitForEventAsync(
            string eventName,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SystemPowerdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PowerdownCount++;
            if (ExitOnPowerdown)
            {
                processes.Status = RecoveryProcessStatus.Stopped;
            }

            return Task.CompletedTask;
        }

        public Task QuitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
