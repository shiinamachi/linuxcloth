using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class QemuSessionHostTests : IDisposable
{
    private const string BootId = "11111111-2222-3333-4444-555555555555";
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}"[..11]);

    [Fact]
    public async Task OwnsProcessesAndCleansSessionAfterGracefulPowerdown()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher) { ExitOnPowerdown = true };
        var progress = new SynchronousProgress();
        var recordStore = new SessionRecordStore();
        var host = CreateHost(launcher, connector, recordStore);

        var session = await host.StartAsync(CreateRequest(paths, progress));

        Assert.Equal(4, launcher.Processes.Count);
        Assert.Contains(launcher.Processes, process => process.Name == "qemu-system-x86_64");
        var confinedQemu = Assert.Single(
            launcher.Specs,
            spec => spec.IdentityExecutablePath == "/usr/bin/qemu-system-x86_64");
        Assert.Equal("/usr/bin/bwrap", confinedQemu.FileName);
        Assert.Contains("--unshare-all", confinedQemu.Arguments);
        Assert.DoesNotContain("--share-net", confinedQemu.Arguments);
        Assert.Contains(SessionState.Running, progress.States);
        var persisted = await recordStore.ReadAsync(paths);
        Assert.Equal(SessionState.Running, persisted.State);
        Assert.Equal("test-image", persisted.ImageId);
        Assert.Equal(4, persisted.Processes.Count);
        Assert.Equal(
            launcher.Processes.Single(process => process.Name == "qemu-system-x86_64").Identity,
            persisted.Processes[SessionProcessNames.Qemu]);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await session.StopAsync(cancelled.Token);

        Assert.Equal(1, connector.Monitor.PowerdownCount);
        Assert.Equal(0, connector.Monitor.QuitCount);
        Assert.All(launcher.Processes, process => Assert.True(process.Disposed));
        Assert.False(Directory.Exists(paths.SessionDirectory));
        Assert.Equal(SessionState.Completed, progress.States[^1]);
    }

    [Fact]
    public async Task EscalatesFromQmpQuitToSigterm()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher);
        var host = CreateHost(launcher, connector);
        var session = await host.StartAsync(CreateRequest(paths));

        await session.StopAsync();

        var qemu = launcher.Processes.Single(process => process.Name == "qemu-system-x86_64");
        Assert.Equal(1, connector.Monitor.PowerdownCount);
        Assert.Equal(1, connector.Monitor.QuitCount);
        Assert.Equal(1, qemu.TerminateCount);
        Assert.Equal(0, qemu.KillCount);
    }

    [Fact]
    public async Task ExposesTheViewerLifetimeToTheApplicationLayer()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher);
        var host = CreateHost(launcher, connector);
        await using var session = await host.StartAsync(CreateRequest(paths));
        var viewer = launcher.Processes.Single(process => process.Name == "remote-viewer");

        viewer.Exit(17);

        Assert.Equal(17, await session.WaitForDisplayExitAsync());
        Assert.False(launcher.Processes.Single(process => process.Name == "qemu-system-x86_64").HasExited);
    }

    [Fact]
    public async Task FailedQmpConnectionCleansEveryStartedProcess()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher) { ThrowOnConnect = true };
        var host = CreateHost(launcher, connector);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CreateRequest(paths)));

        Assert.All(launcher.Processes, process => Assert.True(process.Disposed));
        Assert.False(Directory.Exists(paths.SessionDirectory));
    }

    [Fact]
    public async Task PreservesFailedSessionWhenAStartedProcessCannotBeStopped()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher { UnstoppableName = "qemu-system-x86_64" };
        var connector = new FakeQmpConnector(launcher) { ThrowOnConnect = true };
        var recordStore = new SessionRecordStore();
        var progress = new SynchronousProgress();
        var host = CreateHost(launcher, connector, recordStore);

        await Assert.ThrowsAsync<AggregateException>(
            () => host.StartAsync(CreateRequest(paths, progress)));

        Assert.True(Directory.Exists(paths.SessionDirectory));
        var persisted = await recordStore.ReadAsync(paths);
        Assert.Equal(SessionState.Failed, persisted.State);
        Assert.Contains(SessionProcessNames.Qemu, persisted.Processes.Keys);
        Assert.DoesNotContain(SessionState.Completed, progress.States);
    }

    [Fact]
    public async Task RejectsConfigurationForAnotherSessionBeforeStartingProcesses()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher);
        var host = CreateHost(launcher, connector);
        var request = CreateRequest(paths);
        request = request with
        {
            Configuration = request.Configuration with { SessionId = Guid.NewGuid() },
        };

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(request));

        Assert.Empty(launcher.Processes);
        Assert.False(File.Exists(paths.SessionRecordPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionPaths CreatePaths()
    {
        var paths = SessionPaths.Create(_runtimeRoot, Guid.NewGuid());
        paths.CreateDirectories();
        File.WriteAllText(paths.OverlayPath, "overlay");
        File.WriteAllText(paths.OvmfVariablesPath, "vars");
        Directory.CreateDirectory(paths.SwtpmStateDirectory);
        return paths;
    }

    private static QemuSessionStartRequest CreateRequest(
        SessionPaths paths,
        IProgress<SessionState>? progress = null)
    {
        var toolchain = new QemuToolchain(
            "/usr/bin/qemu-system-x86_64",
            "/usr/bin/qemu-img",
            "/usr/bin/swtpm",
            "/usr/bin/passt",
            "/usr/bin/remote-viewer");
        var launchRequest = new LaunchRequest([ServiceId.Parse("WooriBank")]);
        var baseImagePath = WriteResource(paths.RuntimeRoot, "images/base.qcow2");
        var ovmfCodePath = WriteResource(paths.RuntimeRoot, "firmware/OVMF_CODE.fd");
        var configuration = new QemuLaunchConfiguration(
            toolchain,
            launchRequest,
            paths.SessionId,
            Guid.Parse("057f24df-24e0-4815-8652-3f1e8c62e155"),
            paths.SessionDirectory,
            paths.OverlayPath,
            ovmfCodePath,
            paths.OvmfVariablesPath,
            paths.SwtpmSocketPath,
            paths.QmpSocketPath,
            paths.SpiceSocketPath,
            paths.GuestBridgeSocketPath,
            paths.ConfigDirectory,
            paths.PasstSocketPath);
        return new QemuSessionStartRequest(
            configuration,
            paths,
            "linuxcloth — 우리은행",
            "test-image",
            new string('a', 64),
            new BubblewrapQemuConfinementOptions(
                "/usr/bin/bwrap",
                paths.SessionDirectory,
                baseImagePath,
                ovmfCodePath),
            progress);
    }

    private static string WriteResource(string runtimeRoot, string relativePath)
    {
        var path = Path.Combine(runtimeRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, relativePath);
        }

        return path;
    }

    private static QemuSessionHost CreateHost(
        FakeProcessLauncher launcher,
        FakeQmpConnector connector,
        SessionRecordStore? recordStore = null) =>
        new(
            launcher,
            connector,
            FastPolicy(),
            recordStore,
            new FixedBootIdProvider());

    private static QemuShutdownPolicy FastPolicy() =>
        new(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public List<FakeManagedProcess> Processes { get; } = [];

        public List<ProcessSpec> Specs { get; } = [];

        public string? UnstoppableName { get; init; }

        public Task<IManagedProcess> StartAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Specs.Add(spec);
            var identityExecutable = spec.IdentityExecutablePath ?? spec.FileName;
            var process = new FakeManagedProcess(
                identityExecutable,
                Processes.Count + 100,
                string.Equals(Path.GetFileName(identityExecutable), UnstoppableName, StringComparison.Ordinal));
            Processes.Add(process);

            if (process.Name == "swtpm")
            {
                File.WriteAllText(Path.Combine(spec.WorkingDirectory!, "tpm.sock"), string.Empty);
            }
            else if (process.Name == "passt")
            {
                var index = spec.Arguments.ToList().IndexOf("--socket");
                File.WriteAllText(spec.Arguments[index + 1], string.Empty);
            }

            return Task.FromResult<IManagedProcess>(process);
        }
    }

    private sealed class FakeManagedProcess(string executable, int id, bool unstoppable) : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; } = Path.GetFileName(executable);

        public int Id { get; } = id;

        public ProcessIdentity Identity { get; } = new(id, BootId, id, executable);

        public bool HasExited => _exit.Task.IsCompleted;

        public int TerminateCount { get; private set; }

        public int KillCount { get; private set; }

        public bool Disposed { get; private set; }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            await _exit.Task.WaitAsync(cancellationToken);

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminateCount++;
            if (unstoppable)
            {
                throw new IOException("Synthetic process termination failure.");
            }

            Exit(143);
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KillCount++;
            if (unstoppable)
            {
                throw new IOException("Synthetic process kill failure.");
            }

            Exit(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (unstoppable)
            {
                throw new IOException("Synthetic process disposal failure.");
            }

            Disposed = true;
            Exit(0);
            return ValueTask.CompletedTask;
        }

        public void Exit(int exitCode = 0) => _exit.TrySetResult(exitCode);
    }

    private sealed class FakeQmpConnector(FakeProcessLauncher launcher) : IQmpConnector
    {
        public bool ExitOnPowerdown
        {
            get => Monitor.ExitOnPowerdown;
            init => Monitor.ExitOnPowerdown = value;
        }

        public bool ThrowOnConnect { get; init; }

        public FakeQmpMonitor Monitor { get; } = new(launcher);

        public Task<IQmpMonitor> ConnectAsync(
            string socketPath,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _ = socketPath;
            _ = timeout;
            cancellationToken.ThrowIfCancellationRequested();
            return ThrowOnConnect
                ? throw new InvalidOperationException("QMP failed")
                : Task.FromResult<IQmpMonitor>(Monitor);
        }
    }

    private sealed class FakeQmpMonitor(FakeProcessLauncher launcher) : IQmpMonitor
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
            PowerdownCount++;
            if (ExitOnPowerdown)
            {
                launcher.Processes.Single(process => process.Name == "qemu-system-x86_64").Exit();
            }

            return Task.CompletedTask;
        }

        public Task QuitAsync(CancellationToken cancellationToken = default)
        {
            QuitCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SynchronousProgress : IProgress<SessionState>
    {
        public List<SessionState> States { get; } = [];

        public void Report(SessionState value) => States.Add(value);
    }

    private sealed class FixedBootIdProvider : IBootIdProvider
    {
        public string GetBootId() => BootId;
    }
}
