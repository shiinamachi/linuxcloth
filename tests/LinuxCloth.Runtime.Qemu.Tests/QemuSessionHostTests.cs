using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class QemuSessionHostTests : IDisposable
{
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}"[..11]);

    [Fact]
    public async Task OwnsProcessesAndCleansSessionAfterGracefulPowerdown()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher) { ExitOnPowerdown = true };
        var progress = new SynchronousProgress();
        var host = new QemuSessionHost(launcher, connector, FastPolicy());

        var session = await host.StartAsync(CreateRequest(paths, progress));

        Assert.Equal(4, launcher.Processes.Count);
        Assert.Contains(launcher.Processes, process => process.Name == "qemu-system-x86_64");
        Assert.Contains(SessionState.Running, progress.States);

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
        var host = new QemuSessionHost(launcher, connector, FastPolicy());
        var session = await host.StartAsync(CreateRequest(paths));

        await session.StopAsync();

        var qemu = launcher.Processes.Single(process => process.Name == "qemu-system-x86_64");
        Assert.Equal(1, connector.Monitor.PowerdownCount);
        Assert.Equal(1, connector.Monitor.QuitCount);
        Assert.Equal(1, qemu.TerminateCount);
        Assert.Equal(0, qemu.KillCount);
    }

    [Fact]
    public async Task FailedQmpConnectionCleansEveryStartedProcess()
    {
        var paths = CreatePaths();
        var launcher = new FakeProcessLauncher();
        var connector = new FakeQmpConnector(launcher) { ThrowOnConnect = true };
        var host = new QemuSessionHost(launcher, connector, FastPolicy());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CreateRequest(paths)));

        Assert.All(launcher.Processes, process => Assert.True(process.Disposed));
        Assert.False(Directory.Exists(paths.SessionDirectory));
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
        var configuration = new QemuLaunchConfiguration(
            toolchain,
            launchRequest,
            paths.SessionId,
            Guid.Parse("057f24df-24e0-4815-8652-3f1e8c62e155"),
            paths.SessionDirectory,
            paths.OverlayPath,
            "/usr/share/OVMF/OVMF_CODE.fd",
            paths.OvmfVariablesPath,
            paths.SwtpmSocketPath,
            paths.QmpSocketPath,
            paths.SpiceSocketPath,
            paths.GuestBridgeSocketPath,
            paths.ConfigDirectory,
            paths.PasstSocketPath);
        return new QemuSessionStartRequest(configuration, paths, "linuxcloth — 우리은행", progress);
    }

    private static QemuShutdownPolicy FastPolicy() =>
        new(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

    private sealed class FakeProcessLauncher : IProcessLauncher
    {
        public List<FakeManagedProcess> Processes { get; } = [];

        public Task<IManagedProcess> StartAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = new FakeManagedProcess(spec.FileName, Processes.Count + 100);
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

    private sealed class FakeManagedProcess(string executable, int id) : IManagedProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; } = Path.GetFileName(executable);

        public int Id { get; } = id;

        public ProcessIdentity Identity { get; } = new(id, "boot", id, executable);

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
            Exit(143);
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KillCount++;
            Exit(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
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
}
