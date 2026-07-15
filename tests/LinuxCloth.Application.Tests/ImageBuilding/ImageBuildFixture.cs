using System.Security.Cryptography;
using System.Text.Json;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Storage;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Application.Tests.ImageBuilding;

internal sealed class ImageBuildFixture : IDisposable
{
    public ImageBuildFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"lcb{Guid.NewGuid():N}"[..9]);
        RuntimeRoot = Path.Combine(Path.GetTempPath(), $"lcr{Guid.NewGuid():N}"[..9]);
        Paths = new LinuxClothPaths(
            Path.Combine(Root, "c"),
            Path.Combine(Root, "d"),
            Path.Combine(Root, "k"),
            Path.Combine(Root, "r"));
        Paths.CreateBaseDirectories();
        Registry = new ManagedImageRegistry(Paths);

        WindowsIsoPath = CreateFile("Windows 11; $(not-a-shell).iso", "windows-install-media");
        VirtioWinIsoPath = CreateFile("virtio-win.iso", "virtio-drivers");
        GuestBridgePath = CreateFile("linuxcloth-guest-bridge.exe", "pinned-guest-bridge");
        OvmfCodePath = CreateFile("OVMF_CODE.secboot.fd", "ovmf-code");
        OvmfVariablesPath = CreateFile("OVMF_VARS.fd", "ovmf-vars");
        Toolchain = new WindowsImageBuildToolchain(
            CreateExecutable("qemu-system-x86_64"),
            CreateExecutable("qemu-img"),
            CreateExecutable("swtpm"),
            CreateExecutable("remote-viewer"),
            CreateExecutable("xorriso"),
            CreateExecutable("bwrap"));
        Runner = new ImageBuildProcessRunner(Toolchain.QemuImg);
        Launcher = new ImageBuildProcessLauncher(Toolchain);
        MediaValidator = new StubInstallationMediaValidator();
        BootIdProvider = new StubBootIdProvider();
        ProcessIdentityController = new StubProcessIdentityController();
        Builder = new WindowsImageBuilder(
            Registry,
            Runner,
            Launcher,
            MediaValidator,
            new ImmediateEndpointWaiter(),
            ProcessIdentityController,
            new StubQmpConnector(),
            BootIdProvider,
            runtimeRoot: RuntimeRoot);
    }

    public string Root { get; }
    public string RuntimeRoot { get; }
    public LinuxClothPaths Paths { get; }
    public ManagedImageRegistry Registry { get; }
    public string WindowsIsoPath { get; }
    public string VirtioWinIsoPath { get; }
    public string GuestBridgePath { get; }
    public string OvmfCodePath { get; }
    public string OvmfVariablesPath { get; }
    public WindowsImageBuildToolchain Toolchain { get; }
    public ImageBuildProcessRunner Runner { get; }
    public ImageBuildProcessLauncher Launcher { get; }
    public StubInstallationMediaValidator MediaValidator { get; }
    public StubBootIdProvider BootIdProvider { get; }
    public StubProcessIdentityController ProcessIdentityController { get; }
    public WindowsImageBuilder Builder { get; }

    public WindowsImageBuildRequest CreateRequest(string imageId = "windows-11") =>
        new(
            ImageId.Parse(imageId),
            WindowsIsoPath,
            VirtioWinIsoPath,
            GuestBridgePath,
            OvmfCodePath,
            OvmfVariablesPath,
            Toolchain,
            DiskSizeGiB: 96,
            CpuCount: 4,
            MemoryMiB: 6144);

    public async Task<WindowsImageBuildWorkspace> BeginAsync(string imageId = "windows-11") =>
        await Builder.BeginAsync(CreateRequest(imageId));

    public void Dispose()
    {
        MakeWritable(Root);
        MakeWritable(RuntimeRoot);
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        if (Directory.Exists(RuntimeRoot))
        {
            Directory.Delete(RuntimeRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, string contents)
    {
        var directory = Path.Combine(Root, "resources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private string CreateExecutable(string name)
    {
        var path = CreateFile(name, "test executable");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static void MakeWritable(string root)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

internal sealed class StubInstallationMediaValidator : IInstallationMediaValidator
{
    public int CallCount { get; private set; }

    public Task<ImageBuildFileFingerprint> ValidateWindowsAsync(
        string windowsIsoPath,
        string xorrisoPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default) =>
        ValidateFileAsync(windowsIsoPath, xorrisoPath, bubblewrapPath, cancellationToken);

    public Task<ImageBuildFileFingerprint> ValidateVirtioWinAsync(
        string virtioWinIsoPath,
        string xorrisoPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default) =>
        ValidateFileAsync(virtioWinIsoPath, xorrisoPath, bubblewrapPath, cancellationToken);

    public Task<ValidatedInstallationMedia> ValidateAsync(
        string windowsIsoPath,
        string virtioWinIsoPath,
        string xorrisoPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = xorrisoPath;
        _ = bubblewrapPath;
        CallCount++;
        return Task.FromResult(
            new ValidatedInstallationMedia(
                Fingerprint(windowsIsoPath),
                Fingerprint(virtioWinIsoPath)));
    }

    private Task<ImageBuildFileFingerprint> ValidateFileAsync(
        string path,
        string xorrisoPath,
        string bubblewrapPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = xorrisoPath;
        _ = bubblewrapPath;
        CallCount++;
        return Task.FromResult(Fingerprint(path));
    }

    private static ImageBuildFileFingerprint Fingerprint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        return new ImageBuildFileFingerprint(
            fullPath,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }
}

internal sealed class ImageBuildProcessRunner : IProcessRunner
{
    private readonly string _qemuImgPath;

    public ImageBuildProcessRunner(string qemuImgPath)
    {
        _qemuImgPath = qemuImgPath;
    }

    public List<ProcessSpec> Specs { get; } = [];
    public Dictionary<string, string> ProvisioningFiles { get; } = new(StringComparer.Ordinal);
    public int FailCreateCount { get; set; }
    public bool CancelCreate { get; set; }

    public Task<ProcessResult> RunAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
    {
        Specs.Add(spec);
        if (string.Equals(spec.IdentityExecutablePath ?? spec.FileName, _qemuImgPath, StringComparison.Ordinal))
        {
            var arguments = spec.FileName == _qemuImgPath
                ? spec.Arguments
                : spec.Arguments.Skip(spec.Arguments.ToList().IndexOf("--") + 2).ToArray();
            if (CancelCreate)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (FailCreateCount > 0)
            {
                FailCreateCount--;
                return Task.FromResult(new ProcessResult(2, string.Empty, "simulated qemu-img failure"));
            }

            switch (arguments[0])
            {
                case "create":
                    var destination = arguments[^2];
                    using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                    {
                        stream.Write("QFI\xfb"u8);
                        stream.SetLength(1024 * 1024);
                    }

                    break;
                case "info":
                    return Task.FromResult(
                        new ProcessResult(
                            0,
                            "{\"format\":\"qcow2\",\"virtual-size\":103079215104}",
                            string.Empty));
            }
        }

        if (spec.Arguments.Contains("mkisofs", StringComparer.Ordinal))
        {
            var outputIndex = spec.Arguments.ToList().IndexOf("-o");
            var output = spec.Arguments[outputIndex + 1];
            var sourceDirectory = spec.Arguments[^1];
            foreach (var path in Directory.EnumerateFiles(sourceDirectory))
            {
                ProvisioningFiles[Path.GetFileName(path)] = File.ReadAllText(path);
            }

            File.WriteAllText(output, "test provisioning iso");
        }

        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}

internal sealed class ImageBuildProcessLauncher : IProcessLauncher
{
    private readonly WindowsImageBuildToolchain _toolchain;

    public ImageBuildProcessLauncher(WindowsImageBuildToolchain toolchain)
    {
        _toolchain = toolchain;
    }

    public List<ProcessSpec> Specs { get; } = [];
    public List<ImageBuildManagedProcess> Processes { get; } = [];
    public int QemuExitCode { get; set; }
    public string? FailExecutable { get; set; }
    public bool WriteVerificationResult { get; set; } = true;
    public bool BlockQemuExit { get; set; }
    public bool ViewerExitsImmediately { get; set; }
    public bool FailQemuTerminate { get; set; }

    public Task<IManagedProcess> StartAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Specs.Add(spec);
        var identityExecutable = spec.IdentityExecutablePath ?? spec.FileName;
        if (string.Equals(identityExecutable, FailExecutable, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("simulated process launch failure");
        }

        if (string.Equals(identityExecutable, _toolchain.Swtpm, StringComparison.Ordinal))
        {
            var stateArgument = spec.Arguments[spec.Arguments.ToList().IndexOf("--tpmstate") + 1];
            var stateDirectory = stateArgument["dir=".Length..stateArgument.IndexOf(',', StringComparison.Ordinal)];
            if (!Path.IsPathFullyQualified(stateDirectory))
            {
                stateDirectory = Path.Combine(spec.WorkingDirectory!, stateDirectory);
            }

            Directory.CreateDirectory(stateDirectory);
            File.WriteAllText(Path.Combine(stateDirectory, "tpm2-00.permall"), "initialized state");
        }

        if (WriteVerificationResult &&
            string.Equals(identityExecutable, _toolchain.QemuSystem, StringComparison.Ordinal) &&
            spec.Arguments.Any(argument => argument.Contains("linuxcloth-verification", StringComparison.Ordinal)))
        {
            WriteProbeResult(spec);
        }

        var exitCode = string.Equals(identityExecutable, _toolchain.QemuSystem, StringComparison.Ordinal)
            ? QemuExitCode
            : 0;
        var blocksUntilStopped =
            string.Equals(identityExecutable, _toolchain.Swtpm, StringComparison.Ordinal) ||
            string.Equals(identityExecutable, _toolchain.RemoteViewer, StringComparison.Ordinal) &&
            !ViewerExitsImmediately ||
            string.Equals(identityExecutable, _toolchain.QemuSystem, StringComparison.Ordinal) &&
            BlockQemuExit;
        var process = new ImageBuildManagedProcess(
            1000 + Processes.Count,
            spec,
            exitCode,
            StubBootIdProvider.BootId,
            blocksUntilStopped,
            failTerminate: string.Equals(identityExecutable, _toolchain.QemuSystem, StringComparison.Ordinal) &&
                           FailQemuTerminate);
        Processes.Add(process);
        return Task.FromResult<IManagedProcess>(process);
    }

    public void CompleteRunningQemu()
    {
        var process = Processes.Last(
            candidate => string.Equals(
                candidate.Identity.ExecutablePath,
                _toolchain.QemuSystem,
                StringComparison.Ordinal));
        process.Complete();
    }

    private static void WriteProbeResult(ProcessSpec spec)
    {
        var driveArgument = spec.Arguments.Single(
            argument => argument.Contains("id=linuxcloth-verification", StringComparison.Ordinal));
        var marker = "file=fat:rw:";
        var directory = driveArgument[(driveArgument.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]
            .Replace(",,", ",", StringComparison.Ordinal);
        var probePath = Path.Combine(directory, GuestBridgeProvisioningContract.ProbeFileName);
        using var probe = JsonDocument.Parse(File.ReadAllBytes(probePath));
        var nonce = probe.RootElement.GetProperty("nonce").GetString();
        var hash = probe.RootElement.GetProperty("expectedGuestBridgeSha256").GetString();
        File.WriteAllText(
            Path.Combine(directory, GuestBridgeProvisioningContract.ResultFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = GuestBridgeProvisioningContract.SchemaVersion,
                    nonce,
                    guestBridgeSha256 = hash,
                    guestBridgeVersion = "1.0.0-test",
                    windowsArchitecture = "X64",
                    windowsBuild = 26100,
                    windowsEditionId = "Professional",
                    windowsDisplayVersion = "24H2",
                }));
    }
}

internal sealed class ImageBuildManagedProcess : IManagedProcess
{
    private readonly int _exitCode;
    private readonly TaskCompletionSource<int>? _exitSource;
    private readonly bool _failTerminate;

    public ImageBuildManagedProcess(
        int id,
        ProcessSpec spec,
        int exitCode,
        string bootId,
        bool blocksUntilStopped,
        bool failTerminate)
    {
        Id = id;
        _exitCode = exitCode;
        Identity = new ProcessIdentity(
            id,
            bootId,
            id,
            spec.IdentityExecutablePath ?? Path.GetFullPath(spec.FileName));
        _failTerminate = failTerminate;
        _exitSource = blocksUntilStopped
            ? new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
    }

    public int Id { get; }
    public ProcessIdentity Identity { get; }
    public bool HasExited { get; private set; }
    public bool WasTerminated { get; private set; }
    public bool WasDisposed { get; private set; }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_exitSource is not null)
        {
            var result = await _exitSource.Task.WaitAsync(cancellationToken);
            HasExited = true;
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        HasExited = true;
        return _exitCode;
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_failTerminate)
        {
            throw new IOException("simulated termination failure");
        }

        WasTerminated = true;
        HasExited = true;
        _exitSource?.TrySetResult(_exitCode);
        return Task.CompletedTask;
    }

    public Task KillAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HasExited = true;
        _exitSource?.TrySetResult(_exitCode);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        WasDisposed = true;
        if (!HasExited)
        {
            HasExited = true;
            _exitSource?.TrySetResult(_exitCode);
        }

        return ValueTask.CompletedTask;
    }

    public void Complete()
    {
        HasExited = true;
        _exitSource?.TrySetResult(_exitCode);
    }
}

internal sealed class ImmediateEndpointWaiter : IImageBuildEndpointWaiter
{
    public Task WaitAsync(
        string path,
        IManagedProcess owner,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        _ = path;
        _ = owner;
        _ = timeout;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class StubBootIdProvider : IBootIdProvider
{
    public const string BootId = "11111111-1111-1111-1111-111111111111";

    public string GetBootId() => BootId;
}

internal sealed class StubProcessIdentityController : IProcessIdentityController
{
    public RecoveryProcessStatus Status { get; set; } = RecoveryProcessStatus.Stopped;
    public int TerminateCalls { get; private set; }
    public int KillCalls { get; private set; }

    public Task<RecoveryProcessStatus> InspectAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }

    public Task<RecoverySignalResult> SendTerminateAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TerminateCalls++;
        Status = RecoveryProcessStatus.Stopped;
        return Task.FromResult(RecoverySignalResult.Sent);
    }

    public Task<RecoverySignalResult> SendKillAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KillCalls++;
        Status = RecoveryProcessStatus.Stopped;
        return Task.FromResult(RecoverySignalResult.Sent);
    }

    public Task<RecoveryProcessStatus> WaitForExitAsync(
        ProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }
}

internal sealed class StubQmpConnector : IQmpConnector
{
    public Task<IQmpMonitor> ConnectAsync(
        string socketPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IQmpMonitor>(new IOException("No fake QMP endpoint."));
}
