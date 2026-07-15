using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Application.Storage;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Desktop.Services;

public sealed record DesktopStartupSnapshot(
    CatalogWorkspaceState Catalog,
    IReadOnlyList<ManagedWindowsImage> Images,
    QemuDoctorResult Doctor,
    IReadOnlyList<RecoveryResult> Recovery);

public sealed class DesktopRuntime : IDesktopImageBuildService, IAsyncDisposable
{
    private readonly CatalogWorkspace _catalog;
    private readonly QemuDoctor _doctor;
    private readonly ManagedImageRegistry _images;
    private readonly LinuxClothSessionLauncher _launcher;
    private readonly LinuxClothPaths _paths;
    private readonly QemuDoctorOptions _doctorOptions;
    private readonly RecoverySessionManager _recovery;
    private bool _disposed;

    private DesktopRuntime(
        LinuxClothPaths paths,
        CatalogWorkspace catalog,
        ManagedImageRegistry images,
        QemuDoctor doctor,
        QemuDoctorOptions doctorOptions,
        RecoverySessionManager recovery,
        LinuxClothSessionLauncher launcher)
    {
        _paths = paths;
        _catalog = catalog;
        _images = images;
        _doctor = doctor;
        _doctorOptions = doctorOptions;
        _recovery = recovery;
        _launcher = launcher;
    }

    public CatalogWorkspace Catalog => _catalog;

    public ManagedImageRegistry Images => _images;

    public LinuxClothPaths Paths => _paths;

    public static DesktopRuntime CreateDefault()
    {
        var paths = LinuxClothPaths.FromEnvironment();
        var catalog = new CatalogWorkspace(paths, ResolveOfficialCatalogBundle());
        var images = new ManagedImageRegistry(paths);
        var processRunner = new SystemProcessRunner();
        var runtimeRoot = Path.GetDirectoryName(paths.RuntimeDirectory)
            ?? throw new InvalidOperationException("linuxcloth runtime directory has no parent directory.");
        var doctorOptions = new QemuDoctorOptions { XdgRuntimeDirectory = runtimeRoot };
        var doctor = new QemuDoctor(
            new ExecutableLocator(),
            doctorOptions,
            new SystemQemuDoctorHostProbe());
        var qmpConnector = new QmpConnector();
        var recovery = new RecoverySessionManager(
            new SessionRecordStore(),
            new LinuxProcessIdentityController(),
            qmpConnector);
        var host = new QemuSessionHost(new SystemProcessLauncher(), qmpConnector);
        var launcher = new LinuxClothSessionLauncher(
            paths,
            catalog,
            new DoctorLaunchPrerequisiteSource(doctor),
            new ManagedImageLaunchSource(images),
            new QemuSessionArtifactService(new SessionArtifactPreparer(processRunner)),
            new GuestConfigurationService(),
            new QemuVmSessionStarter(host));
        return new DesktopRuntime(paths, catalog, images, doctor, doctorOptions, recovery, launcher);
    }

    public async Task<DesktopStartupSnapshot> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _paths.CreateBaseDirectories();
        var recovery = await _recovery.RecoverAllAsync(_paths.RuntimeDirectory, cancellationToken)
            .ConfigureAwait(false);
        var catalog = await _catalog.InitializeWithBundledRefreshAsync(cancellationToken)
            .ConfigureAwait(false);
        var doctor = await _doctor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        var images = await _images.ListAsync(cancellationToken).ConfigureAwait(false);
        return new DesktopStartupSnapshot(catalog, images, doctor, recovery);
    }

    public Task<QemuDoctorResult> InspectHostAsync(CancellationToken cancellationToken = default) =>
        _doctor.InspectDetailedAsync(cancellationToken);

    public Task<IReadOnlyList<ManagedWindowsImage>> ListImagesAsync(
        CancellationToken cancellationToken = default) =>
        _images.ListAsync(cancellationToken);

    public async Task<DesktopImageBuildDefaults> GetImageBuildDefaultsAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var guestBridgePath = ResolveDefaultGuestBridgePath();
        var firmware = new FirmwareDescriptorResolver(_doctorOptions.FirmwareDescriptorDirectory)
            .Resolve()
            .Pair;
        return await Task.FromResult(
                new DesktopImageBuildDefaults(
                    guestBridgePath,
                    File.Exists(guestBridgePath),
                    firmware?.Executable.Path,
                    firmware?.NvramTemplate.Path))
            .ConfigureAwait(false);
    }

    public async Task<ManagedWindowsImage> BuildImageAsync(
        DesktopImageBuildRequest request,
        IProgress<DesktopImageBuildProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        _paths.CreateBaseDirectories();

        ValidateSelectedFirmware(request);
        var toolchain = await ResolveImageBuildToolchainAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(new DesktopImageBuildProgress(WindowsImageBuildPhase.Preparing, null));
        var builder = CreateImageBuilder();
        var workspace = await builder.BeginAsync(
                new WindowsImageBuildRequest(
                    request.ImageId,
                    request.WindowsIsoPath,
                    request.VirtioWinIsoPath,
                    request.GuestBridgeExecutablePath,
                    request.OvmfCodePath,
                    request.OvmfVariablesTemplatePath,
                    toolchain,
                    request.DiskSizeGiB,
                    request.CpuCount,
                    request.MemoryMiB),
                cancellationToken)
            .ConfigureAwait(false);
        return await CompleteImageBuildAsync(builder, workspace, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ManagedWindowsImage> ResumeImageBuildAsync(
        ImageId imageId,
        string stagingDirectory,
        IProgress<DesktopImageBuildProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        _paths.CreateBaseDirectories();

        var builder = CreateImageBuilder();
        var workspace = await builder.ResumeAsync(imageId, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (workspace.State.Phase is WindowsImageBuildPhase.InstallerRunning or
            WindowsImageBuildPhase.VerificationRunning)
        {
            progress.Report(
                new DesktopImageBuildProgress(
                    workspace.State.Phase,
                    workspace.Staging.DirectoryPath,
                    IsRecovery: true));
            workspace = await builder.RecoverInterruptedRunAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CompleteImageBuildAsync(builder, workspace, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IRunningLinuxClothSession> LaunchAsync(
        LaunchRequest request,
        ImageId imageId,
        IProgress<SessionState>? progress = null,
        CancellationToken cancellationToken = default) =>
        _launcher.LaunchAsync(request, imageId, progress, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _catalog.Dispose();
        return ValueTask.CompletedTask;
    }

    private static OfficialCatalogBundle ResolveOfficialCatalogBundle()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("LINUXCLOTH_CATALOG_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return CreateBundleFromRoot(configuredRoot);
        }

        var installed = Path.Combine(AppContext.BaseDirectory, "catalog");
        if (File.Exists(Path.Combine(installed, "Catalog.xml")))
        {
            return CreateBundleFromDocsDirectory(installed);
        }

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var checkout = Path.Combine(directory.FullName, "vendor", "TableClothCatalog");
            if (File.Exists(Path.Combine(checkout, "docs", "Catalog.xml")))
            {
                return OfficialCatalogBundle.FromPinnedCheckout(checkout);
            }
        }

        throw new DirectoryNotFoundException(
            "공식 TableCloth 카탈로그를 찾지 못했습니다. LINUXCLOTH_CATALOG_ROOT를 설정하세요.");
    }

    private static OfficialCatalogBundle CreateBundleFromRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return File.Exists(Path.Combine(fullRoot, "docs", "Catalog.xml"))
            ? OfficialCatalogBundle.FromPinnedCheckout(fullRoot)
            : CreateBundleFromDocsDirectory(fullRoot);
    }

    private static OfficialCatalogBundle CreateBundleFromDocsDirectory(string directory) =>
        OfficialCatalogBundle.FromPinnedDocsDirectory(directory);

    private WindowsImageBuilder CreateImageBuilder() =>
        new(
            _images,
            new SystemProcessRunner(),
            new SystemProcessLauncher(),
            runtimeRoot: Path.Combine(_paths.RuntimeDirectory, "image-build"));

    private async Task<WindowsImageBuildToolchain> ResolveImageBuildToolchainAsync(
        CancellationToken cancellationToken)
    {
        var result = await _doctor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        string[] requiredCodes =
        [
            QemuDoctorCheckCodes.Platform,
            QemuDoctorCheckCodes.Kvm,
            QemuDoctorCheckCodes.QemuSystem,
            QemuDoctorCheckCodes.QemuImg,
            QemuDoctorCheckCodes.Swtpm,
            QemuDoctorCheckCodes.RemoteViewer,
            QemuDoctorCheckCodes.Bubblewrap,
            QemuDoctorCheckCodes.Firmware,
            QemuDoctorCheckCodes.RuntimeDirectory,
            QemuDoctorCheckCodes.Xorriso,
        ];
        var unavailable = requiredCodes
            .Where(code => result.Report.Checks.Any(
                check => string.Equals(check.Name, code, StringComparison.Ordinal) && !check.IsAvailable))
            .ToArray();
        if (unavailable.Length > 0)
        {
            throw new InvalidOperationException(
                $"Windows 이미지 생성에 필요한 호스트 항목이 없습니다: {string.Join(", ", unavailable)}");
        }

        return new WindowsImageBuildToolchain(
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.QemuSystem),
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.QemuImg),
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.Swtpm),
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.RemoteViewer),
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.Xorriso),
            RequireDoctorPath(result.Report, QemuDoctorCheckCodes.Bubblewrap));
    }

    private static async Task<ManagedWindowsImage> CompleteImageBuildAsync(
        WindowsImageBuilder builder,
        WindowsImageBuildWorkspace workspace,
        IProgress<DesktopImageBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(
            new DesktopImageBuildProgress(
                workspace.State.Phase,
                workspace.Staging.DirectoryPath));
        if (workspace.State.Phase == WindowsImageBuildPhase.Prepared)
        {
            progress.Report(
                new DesktopImageBuildProgress(
                    WindowsImageBuildPhase.InstallerRunning,
                    workspace.Staging.DirectoryPath));
            workspace = await builder.RunInstallerAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(
                new DesktopImageBuildProgress(
                    workspace.State.Phase,
                    workspace.Staging.DirectoryPath));
        }

        if (workspace.State.Phase == WindowsImageBuildPhase.ReadyToVerify)
        {
            progress.Report(
                new DesktopImageBuildProgress(
                    WindowsImageBuildPhase.VerificationRunning,
                    workspace.Staging.DirectoryPath));
            workspace = await builder.RunVerificationAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(
                new DesktopImageBuildProgress(
                    workspace.State.Phase,
                    workspace.Staging.DirectoryPath));
        }

        if (workspace.State.Phase != WindowsImageBuildPhase.ReadyToFinalize)
        {
            throw new WindowsImageBuildException(
                $"이미지 빌드를 계속할 수 없는 상태입니다: {workspace.State.Phase}",
                workspace.Staging);
        }

        return await builder.FinalizeAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    private static string RequireDoctorPath(DoctorReport report, string code) =>
        report.FindPath(code) ?? throw new InvalidOperationException(
            $"필수 실행 파일 경로를 찾지 못했습니다: {code}");

    private void ValidateSelectedFirmware(DesktopImageBuildRequest request)
    {
        var verified = new FirmwareDescriptorResolver(_doctorOptions.FirmwareDescriptorDirectory)
            .Resolve()
            .Pair;
        DesktopFirmwareSelectionValidator.Validate(
            verified,
            request.OvmfCodePath,
            request.OvmfVariablesTemplatePath);
    }

    private static string ResolveDefaultGuestBridgePath()
    {
        var configured = Environment.GetEnvironmentVariable("LINUXCLOTH_GUEST_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
        {
            var normalized = Path.GetFullPath(configured);
            if (File.Exists(normalized))
            {
                return normalized;
            }
        }

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "guest", GuestBridgeProvisioningContract.ExecutableFileName),
            Path.Combine(baseDirectory, GuestBridgeProvisioningContract.ExecutableFileName),
            Path.GetFullPath(
                Path.Combine(
                    baseDirectory,
                    "..",
                    "guest",
                    GuestBridgeProvisioningContract.ExecutableFileName)),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
