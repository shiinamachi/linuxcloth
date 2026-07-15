using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Application.Storage;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Cli;

public sealed class DefaultCliCommandServices : ICliCommandServices
{
    private readonly CatalogBundleResolver _catalogBundleResolver;
    private readonly LinuxClothPaths _paths;

    public DefaultCliCommandServices(
        LinuxClothPaths? paths = null,
        CatalogBundleResolver? catalogBundleResolver = null)
    {
        _paths = paths ?? LinuxClothPaths.FromEnvironment();
        _catalogBundleResolver = catalogBundleResolver ?? new CatalogBundleResolver();
    }

    public async Task<DoctorReport> InspectHostAsync(CancellationToken cancellationToken)
    {
        _paths.CreateBaseDirectories();
        return await CreateDoctor().InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogServiceEntry>> QueryCatalogAsync(
        string? query,
        CatalogCategory? category,
        string? catalogRoot,
        CancellationToken cancellationToken)
    {
        var resolved = CreateCatalogWorkspace(catalogRoot);
        using var workspace = resolved.Workspace;
        _ = await InitializeCatalogAsync(resolved, cancellationToken).ConfigureAwait(false);
        return workspace.Search(query, category);
    }

    public Task<IReadOnlyList<ManagedWindowsImage>> ListImagesAsync(
        CancellationToken cancellationToken)
    {
        _paths.CreateBaseDirectories();
        return new ManagedImageRegistry(_paths).ListAsync(cancellationToken);
    }

    public Task<ImageVerificationResult> VerifyImageAsync(
        ImageId imageId,
        CancellationToken cancellationToken)
    {
        _paths.CreateBaseDirectories();
        return new ManagedImageRegistry(_paths).VerifyAsync(imageId, cancellationToken);
    }

    public async Task<ManagedWindowsImage> BuildImageAsync(
        ImageBuildStartCommand command,
        IProgress<ImageBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(progress);
        _paths.CreateBaseDirectories();

        var environment = await ResolveImageBuildEnvironmentAsync(cancellationToken)
            .ConfigureAwait(false);
        var builder = CreateImageBuilder();
        progress.Report(new ImageBuildProgress(
            WindowsImageBuildPhase.Preparing,
            StagingDirectory: null));
        var request = new WindowsImageBuildRequest(
            command.ImageId,
            command.WindowsIsoPath,
            command.VirtioWinIsoPath,
            command.GuestBridgeExecutablePath,
            environment.Firmware.Executable.Path,
            environment.Firmware.NvramTemplate.Path,
            environment.Toolchain,
            command.DiskSizeGiB,
            command.CpuCount,
            command.MemoryMiB);
        var workspace = await builder.BeginAsync(request, cancellationToken).ConfigureAwait(false);
        return await CompleteImageBuildAsync(builder, workspace, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ManagedWindowsImage> ResumeImageBuildAsync(
        ImageBuildResumeCommand command,
        IProgress<ImageBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(progress);
        _paths.CreateBaseDirectories();

        var builder = CreateImageBuilder();
        var workspace = await builder.ResumeAsync(
                command.ImageId,
                command.StagingDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (workspace.State.Phase is WindowsImageBuildPhase.InstallerRunning or
            WindowsImageBuildPhase.VerificationRunning)
        {
            throw new WindowsImageBuildException(
                "중단되었거나 실행 중인 이미지 빌드입니다. 프로세스 identity를 검증해 복구하려면 image build recover를 실행하세요.",
                workspace.Staging);
        }

        return await CompleteImageBuildAsync(builder, workspace, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WindowsImageBuildWorkspace> RecoverImageBuildAsync(
        ImageBuildRecoverCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        _paths.CreateBaseDirectories();

        var builder = CreateImageBuilder();
        var workspace = await builder.ResumeAsync(
                command.ImageId,
                command.StagingDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        return await builder.RecoverInterruptedRunAsync(workspace, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RecoveryResult>> CleanupSessionsAsync(
        CancellationToken cancellationToken)
    {
        _paths.CreateBaseDirectories();
        var recovery = new RecoverySessionManager(
            new SessionRecordStore(),
            new LinuxProcessIdentityController(),
            new QmpConnector());
        return recovery.RecoverAllAsync(_paths.RuntimeDirectory, cancellationToken);
    }

    public async Task<Guid> RunSessionAsync(
        RunCommand command,
        IProgress<SessionState> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(progress);

        _paths.CreateBaseDirectories();
        var resolved = CreateCatalogWorkspace(command.CatalogRoot);
        using var workspace = resolved.Workspace;
        _ = await InitializeCatalogAsync(resolved, cancellationToken).ConfigureAwait(false);

        var processRunner = new SystemProcessRunner();
        var qmpConnector = new QmpConnector();
        var launcher = new LinuxClothSessionLauncher(
            _paths,
            workspace,
            new DoctorLaunchPrerequisiteSource(CreateDoctor()),
            new ManagedImageLaunchSource(new ManagedImageRegistry(_paths)),
            new QemuSessionArtifactService(new SessionArtifactPreparer(processRunner)),
            new GuestConfigurationService(),
            new QemuVmSessionStarter(new QemuSessionHost(
                new SystemProcessLauncher(),
                qmpConnector)));

        var request = new LaunchRequest(
            command.ServiceIds,
            command.CpuCount,
            command.MemoryMiB,
            DisplayMode.Spice,
            command.NetworkEnabled,
            command.ClipboardEnabled);

        await using var session = await launcher.LaunchAsync(
                request,
                command.ImageId,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return session.SessionId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private ResolvedCatalogWorkspace CreateCatalogWorkspace(string? catalogRoot)
    {
        var resolution = _catalogBundleResolver.ResolveWithPolicy(catalogRoot);
        return new ResolvedCatalogWorkspace(
            new CatalogWorkspace(_paths, resolution.Bundle),
            resolution.Bundle,
            resolution.UsePolicy);
    }

    private static async Task<CatalogWorkspaceState> InitializeCatalogAsync(
        ResolvedCatalogWorkspace resolved,
        CancellationToken cancellationToken)
    {
        if (resolved.UsePolicy == CatalogBundleUsePolicy.RequiredOverride)
        {
            return await resolved.Workspace.PromoteBundleAsync(resolved.Bundle, cancellationToken)
                .ConfigureAwait(false);
        }

        return await resolved.Workspace.InitializeWithBundledRefreshAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private QemuDoctor CreateDoctor()
    {
        return new QemuDoctor(
            new ExecutableLocator(),
            CreateDoctorOptions(),
            new SystemQemuDoctorHostProbe());
    }

    private QemuDoctorOptions CreateDoctorOptions()
    {
        var runtimeRoot = Path.GetDirectoryName(_paths.RuntimeDirectory)
            ?? throw new InvalidOperationException("linuxcloth runtime directory has no parent directory.");
        return new QemuDoctorOptions { XdgRuntimeDirectory = runtimeRoot };
    }

    private WindowsImageBuilder CreateImageBuilder() =>
        new(
            new ManagedImageRegistry(_paths),
            new SystemProcessRunner(),
            new SystemProcessLauncher(),
            runtimeRoot: Path.Combine(_paths.RuntimeDirectory, "image-build"));

    private async Task<ImageBuildEnvironment> ResolveImageBuildEnvironmentAsync(
        CancellationToken cancellationToken)
    {
        var result = await CreateDoctor().InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        var requiredCodes = new HashSet<string>(StringComparer.Ordinal)
        {
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
        };
        var unavailable = result.Report.Checks
            .Where(check => requiredCodes.Contains(check.Name) && !check.IsAvailable)
            .Select(static check => check.Name)
            .ToArray();
        if (unavailable.Length > 0)
        {
            throw new CliCommandException(
                CliExitCode.HostUnavailable,
                $"Windows 이미지 빌드 요구사항이 없습니다: {string.Join(", ", unavailable)}");
        }

        var firmware = new FirmwareDescriptorResolver(CreateDoctorOptions().FirmwareDescriptorDirectory)
            .Resolve()
            .Pair ?? throw new CliCommandException(
                CliExitCode.HostUnavailable,
                "호환되는 Secure Boot OVMF 펌웨어를 찾지 못했습니다.");
        var report = result.Report;
        var toolchain = new WindowsImageBuildToolchain(
            RequirePath(report, QemuDoctorCheckCodes.QemuSystem),
            RequirePath(report, QemuDoctorCheckCodes.QemuImg),
            RequirePath(report, QemuDoctorCheckCodes.Swtpm),
            RequirePath(report, QemuDoctorCheckCodes.RemoteViewer),
            RequirePath(report, QemuDoctorCheckCodes.Xorriso),
            RequirePath(report, QemuDoctorCheckCodes.Bubblewrap));
        return new ImageBuildEnvironment(toolchain, firmware);
    }

    private static async Task<ManagedWindowsImage> CompleteImageBuildAsync(
        WindowsImageBuilder builder,
        WindowsImageBuildWorkspace workspace,
        IProgress<ImageBuildProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new ImageBuildProgress(
            workspace.State.Phase,
            workspace.Staging.DirectoryPath));
        if (workspace.State.Phase == WindowsImageBuildPhase.Prepared)
        {
            progress.Report(new ImageBuildProgress(
                WindowsImageBuildPhase.InstallerRunning,
                workspace.Staging.DirectoryPath));
            workspace = await builder.RunInstallerAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new ImageBuildProgress(
                workspace.State.Phase,
                workspace.Staging.DirectoryPath));
        }

        if (workspace.State.Phase == WindowsImageBuildPhase.ReadyToVerify)
        {
            progress.Report(new ImageBuildProgress(
                WindowsImageBuildPhase.VerificationRunning,
                workspace.Staging.DirectoryPath));
            workspace = await builder.RunVerificationAsync(workspace, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(new ImageBuildProgress(
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

    private static string RequirePath(DoctorReport report, string code) =>
        report.FindPath(code) ?? throw new CliCommandException(
            CliExitCode.HostUnavailable,
            $"필수 실행 파일을 찾지 못했습니다: {code}");

    private sealed record ImageBuildEnvironment(
        WindowsImageBuildToolchain Toolchain,
        FirmwarePair Firmware);

    private sealed record ResolvedCatalogWorkspace(
        CatalogWorkspace Workspace,
        OfficialCatalogBundle Bundle,
        CatalogBundleUsePolicy UsePolicy);
}
