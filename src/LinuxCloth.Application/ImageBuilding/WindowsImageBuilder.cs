using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Application.ImageBuilding;

public sealed class WindowsImageBuilder
{
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan InstallationVmTimeout = TimeSpan.FromHours(4);
    private static readonly TimeSpan ProcessStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VerificationVmTimeout = TimeSpan.FromMinutes(30);
    private readonly ManagedImageRegistry _registry;
    private readonly IProcessRunner _processRunner;
    private readonly IProcessLauncher _processLauncher;
    private readonly IInstallationMediaValidator _mediaValidator;
    private readonly IImageBuildEndpointWaiter _endpointWaiter;
    private readonly IProcessIdentityController _processIdentityController;
    private readonly IQmpConnector _qmpConnector;
    private readonly IBootIdProvider _bootIdProvider;
    private readonly TimeProvider _timeProvider;
    private readonly string _runtimeRoot;

    public WindowsImageBuilder(
        ManagedImageRegistry registry,
        IProcessRunner processRunner,
        IProcessLauncher processLauncher,
        IInstallationMediaValidator? mediaValidator = null,
        IImageBuildEndpointWaiter? endpointWaiter = null,
        IProcessIdentityController? processIdentityController = null,
        IQmpConnector? qmpConnector = null,
        IBootIdProvider? bootIdProvider = null,
        TimeProvider? timeProvider = null,
        string? runtimeRoot = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _mediaValidator = mediaValidator ?? new XorrisoInstallationMediaValidator(processRunner);
        _endpointWaiter = endpointWaiter ?? new ImageBuildEndpointWaiter();
        _processIdentityController = processIdentityController ?? new LinuxProcessIdentityController();
        _qmpConnector = qmpConnector ?? new QmpConnector();
        _bootIdProvider = bootIdProvider ?? new LinuxBootIdProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
        var normalizedRuntimeRoot = ImageBuildPathGuard.NormalizeAbsolute(
            runtimeRoot ?? GetDefaultRuntimeRoot(),
            "image-builder runtime root");
        if (normalizedRuntimeRoot.Contains(',', StringComparison.Ordinal))
        {
            throw new WindowsImageBuildException(
                "The image-builder runtime root cannot contain a comma because swtpm uses comma-delimited options.");
        }

        if (_registry.ImagesDirectory.Contains(',', StringComparison.Ordinal))
        {
            throw new WindowsImageBuildException(
                "The managed image registry cannot contain a comma because swtpm uses comma-delimited options.");
        }

        if (IsSameOrDescendant(normalizedRuntimeRoot, _registry.ImagesDirectory) ||
            IsSameOrDescendant(_registry.ImagesDirectory, normalizedRuntimeRoot))
        {
            throw new WindowsImageBuildException(
                "The image-builder runtime root and managed image registry must be separate directory trees.");
        }

        _runtimeRoot = EnsureRuntimeRoot(normalizedRuntimeRoot);
        var longestSocket = Path.Combine(_runtimeRoot, new string('0', 12), "sockets", "s.sock");
        if (Encoding.UTF8.GetByteCount(longestSocket) > 100)
        {
            throw new PathTooLongException(
                $"The image-builder runtime root is too long for safe Unix sockets: {_runtimeRoot}");
        }
    }

    public async Task<WindowsImageBuildWorkspace> BeginAsync(
        WindowsImageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeAndValidateRequest(request);
        var media = await _mediaValidator.ValidateAsync(
                normalized.WindowsIsoPath,
                normalized.VirtioWinIsoPath,
                normalized.Toolchain.Xorriso,
                normalized.Toolchain.Bubblewrap,
                cancellationToken)
            .ConfigureAwait(false);
        var ovmfCode = await ImageBuildFileHasher.HashAsync(
                normalized.OvmfCodePath,
                "OVMF code image",
                cancellationToken)
            .ConfigureAwait(false);
        var guestBridge = await ImageBuildFileHasher.HashAsync(
                normalized.GuestBridgeExecutablePath,
                "GuestBridge executable",
                cancellationToken)
            .ConfigureAwait(false);
        var ovmfVariables = await ImageBuildFileHasher.HashAsync(
                normalized.OvmfVariablesTemplatePath,
                "OVMF variables template",
                cancellationToken)
            .ConfigureAwait(false);

        ImageRegistrationStaging? staging = null;
        try
        {
            staging = _registry.CreateStaging(normalized.ImageId);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var state = new WindowsImageBuildState(
                WindowsImageBuildState.CurrentSchemaVersion,
                normalized.ImageId,
                Guid.NewGuid(),
                WindowsImageBuildPhase.Preparing,
                media.WindowsIso,
                media.VirtioWinIso,
                guestBridge,
                ovmfCode,
                ovmfVariables,
                normalized.Toolchain,
                normalized.Installation!,
                normalized.DiskSizeGiB,
                normalized.CpuCount,
                normalized.MemoryMiB,
                null,
                null,
                new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal),
                null,
                null,
                now,
                now);
            var workspace = CreateWorkspace(staging, state);
            ValidateSocketPaths(workspace);
            await WindowsImageBuildStateStore.WriteAsync(staging, state, cancellationToken)
                .ConfigureAwait(false);
            return await CompletePreparationAsync(workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (staging is not null)
        {
            throw new WindowsImageBuildCanceledException(
                "Windows image preparation was canceled; its staging area was preserved.",
                exception,
                staging);
        }
        catch (WindowsImageBuildException exception) when (staging is not null && exception.Staging is null)
        {
            throw new WindowsImageBuildException(exception.Message, exception, staging);
        }
        catch (Exception exception) when (staging is not null)
        {
            throw new WindowsImageBuildException(
                "Windows image preparation failed; its staging area was preserved.",
                exception,
                staging);
        }
    }

    public async Task<WindowsImageBuildWorkspace> ResumeAsync(
        ImageId imageId,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        var staging = _registry.OpenStaging(imageId, stagingDirectory);
        using var operationLock = AcquireOperationLock(staging);
        try
        {
            var state = await WindowsImageBuildStateStore.ReadAsync(staging, cancellationToken)
                .ConfigureAwait(false);
            var workspace = CreateWorkspace(staging, state);
            ValidateSocketPaths(workspace);

            switch (state.Phase)
            {
                case WindowsImageBuildPhase.Preparing:
                    await ValidateDependenciesAsync(
                            state,
                            includeMedia: true,
                            includeVariables: true,
                            includeGuestBridge: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return await CompletePreparationAsync(workspace, cancellationToken).ConfigureAwait(false);
                case WindowsImageBuildPhase.Prepared:
                    await ValidateDependenciesAsync(
                            state,
                            includeMedia: true,
                            includeVariables: false,
                            includeGuestBridge: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidatePreparedArtifacts(staging);
                    return workspace;
                case WindowsImageBuildPhase.InstallerRunning:
                case WindowsImageBuildPhase.VerificationRunning:
                    ValidatePreparedArtifacts(staging);
                    return workspace;
                case WindowsImageBuildPhase.ReadyToVerify:
                    await ValidateDependenciesAsync(
                            state,
                            includeMedia: false,
                            includeVariables: false,
                            includeGuestBridge: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidatePreparedArtifacts(staging);
                    return workspace;
                case WindowsImageBuildPhase.ReadyToFinalize:
                    await ValidateDependenciesAsync(
                            state,
                            includeMedia: false,
                            includeVariables: false,
                            includeGuestBridge: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    ValidatePreparedArtifacts(staging);
                    return workspace;
                default:
                    throw new WindowsImageBuildException("The image-build phase is unsupported.", staging);
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new WindowsImageBuildCanceledException(
                "Windows image resume was canceled; its staging area was preserved.",
                exception,
                staging);
        }
        catch (WindowsImageBuildException exception) when (exception.Staging is null)
        {
            throw new WindowsImageBuildException(exception.Message, exception, staging);
        }
        catch (Exception exception)
        {
            throw new WindowsImageBuildException(
                "Windows image resume failed; its staging area was preserved.",
                exception,
                staging);
        }
    }

    public async Task<WindowsImageBuildWorkspace> RunInstallerAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var operationLock = AcquireOperationLock(workspace.Staging);
        var current = await ReloadWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (current.State.Phase == WindowsImageBuildPhase.InstallerRunning)
        {
            throw new WindowsImageBuildException(
                "The installer has durable active-process state. Recover it before retrying the build.",
                current.Staging);
        }

        if (current.State.Phase != WindowsImageBuildPhase.Prepared)
        {
            throw new WindowsImageBuildException(
                "The Windows installer can run only from a prepared image staging area.",
                current.Staging);
        }

        await ValidateDependenciesAsync(
                current.State,
                includeMedia: true,
                includeVariables: false,
                includeGuestBridge: true,
                cancellationToken)
            .ConfigureAwait(false);
        ValidatePreparedArtifacts(current.Staging);
        PrepareRuntimeDirectory(current);
        await PrepareProvisioningMediaAsync(current, cancellationToken).ConfigureAwait(false);

        var running = current with
        {
            State = CreateRunningState(
                current.State,
                WindowsImageBuildPhase.InstallerRunning,
                verificationNonce: null),
        };
        await WindowsImageBuildStateStore.WriteAsync(running.Staging, running.State, cancellationToken)
            .ConfigureAwait(false);
        running = await ExecuteVmAsync(
                running,
                ImageBuilderCommandFactory.BuildQemu,
                WindowsImageBuildPhase.Prepared,
                InstallationVmTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureTpmWasInitialized(running.Staging);
            await ValidateBaseImageAsync(running, cancellationToken).ConfigureAwait(false);
            ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(running.RuntimeDirectory);
            var ready = running with
            {
                State = CompleteRunningState(
                    running.State,
                    WindowsImageBuildPhase.ReadyToVerify),
            };
            await WindowsImageBuildStateStore.WriteAsync(
                    ready.Staging,
                    ready.State,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ready;
        }
        catch (Exception completionFailure)
        {
            if (running.State.Phase != WindowsImageBuildPhase.InstallerRunning)
            {
                throw;
            }

            var prepared = running with
            {
                State = CompleteRunningState(
                    running.State,
                    WindowsImageBuildPhase.Prepared),
            };
            try
            {
                await WindowsImageBuildStateStore.WriteAsync(
                        prepared.Staging,
                        prepared.State,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception stateFailure)
            {
                throw new WindowsImageBuildException(
                    "The installer completed but TPM validation and state recovery both failed; its staging area was preserved.",
                    new AggregateException(completionFailure, stateFailure),
                    running.Staging);
            }

            throw new WindowsImageBuildException(
                "The installer completed but its base image could not be validated; its staging area was preserved.",
                completionFailure,
                running.Staging);
        }
    }

    public async Task<WindowsImageBuildWorkspace> RunVerificationAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var operationLock = AcquireOperationLock(workspace.Staging);
        var current = await ReloadWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (current.State.Phase == WindowsImageBuildPhase.VerificationRunning)
        {
            throw new WindowsImageBuildException(
                "Image verification has durable active-process state. Recover it before retrying.",
                current.Staging);
        }

        if (current.State.Phase != WindowsImageBuildPhase.ReadyToVerify)
        {
            throw new WindowsImageBuildException(
                "The base-only GuestBridge verification can run only after the Windows installer completes.",
                current.Staging);
        }

        await ValidateDependenciesAsync(
                current.State,
                includeMedia: false,
                includeVariables: false,
                includeGuestBridge: true,
                cancellationToken)
            .ConfigureAwait(false);
        ValidatePreparedArtifacts(current.Staging);
        await ValidateBaseImageAsync(current, cancellationToken).ConfigureAwait(false);
        PrepareRuntimeDirectory(current);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        PrepareVerificationProbe(current, nonce);

        var running = current with
        {
            State = CreateRunningState(
                current.State,
                WindowsImageBuildPhase.VerificationRunning,
                nonce),
        };
        await WindowsImageBuildStateStore.WriteAsync(running.Staging, running.State, cancellationToken)
            .ConfigureAwait(false);
        running = await ExecuteVmAsync(
                running,
                ImageBuilderCommandFactory.BuildVerificationQemu,
                WindowsImageBuildPhase.ReadyToVerify,
                VerificationVmTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var verifiedEnvironment = ValidateVerificationResult(running);
            await ValidateBaseImageAsync(running, cancellationToken).ConfigureAwait(false);
            ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(running.RuntimeDirectory);
            var ready = running with
            {
                State = CompleteRunningState(
                    running.State,
                    WindowsImageBuildPhase.ReadyToFinalize,
                    verifiedEnvironment),
            };
            await WindowsImageBuildStateStore.WriteAsync(
                    ready.Staging,
                    ready.State,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ready;
        }
        catch (Exception completionFailure)
        {
            var retry = running with
            {
                State = CompleteRunningState(
                    running.State,
                    WindowsImageBuildPhase.ReadyToVerify),
            };
            try
            {
                await WindowsImageBuildStateStore.WriteAsync(
                        retry.Staging,
                        retry.State,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception stateFailure)
            {
                throw new WindowsImageBuildException(
                    "GuestBridge verification failed and its retry state could not be persisted.",
                    new AggregateException(completionFailure, stateFailure),
                    running.Staging);
            }

            throw new WindowsImageBuildException(
                "The installed image did not complete the pinned GuestBridge readiness handshake; promotion was blocked.",
                completionFailure,
                running.Staging);
        }
    }

    public async Task<WindowsImageBuildWorkspace> RecoverInterruptedRunAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var operationLock = AcquireOperationLock(workspace.Staging);
        var current = await ReloadWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (current.State.Phase is not (
                WindowsImageBuildPhase.InstallerRunning or
                WindowsImageBuildPhase.VerificationRunning))
        {
            throw new WindowsImageBuildException(
                "Only an interrupted installer or verification run can be recovered.",
                current.Staging);
        }

        await RecoverPersistedProcessesAsync(current, cancellationToken).ConfigureAwait(false);
        ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(current.RuntimeDirectory);
        var recoveryPhase = current.State.Phase == WindowsImageBuildPhase.InstallerRunning
            ? WindowsImageBuildPhase.Prepared
            : WindowsImageBuildPhase.ReadyToVerify;
        var recovered = current with
        {
            State = CompleteRunningState(current.State, recoveryPhase),
        };
        await WindowsImageBuildStateStore.WriteAsync(recovered.Staging, recovered.State, cancellationToken)
            .ConfigureAwait(false);
        return recovered;
    }

    public async Task<ManagedWindowsImage> FinalizeAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var operationLock = AcquireOperationLock(workspace.Staging);
        var current = await ReloadWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (current.State.Phase != WindowsImageBuildPhase.ReadyToFinalize)
        {
            throw new WindowsImageBuildException(
                "The image can be finalized only after the interactive installer exits successfully.",
                current.Staging);
        }

        await ValidateDependenciesAsync(
                current.State,
                includeMedia: false,
                includeVariables: false,
                includeGuestBridge: false,
                cancellationToken)
            .ConfigureAwait(false);
        ValidatePreparedArtifacts(current.Staging);
        EnsureTpmWasInitialized(current.Staging);
        await ValidateBaseImageAsync(current, cancellationToken).ConfigureAwait(false);
        try
        {
            ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(current.RuntimeDirectory);
            WindowsImageBuildStateStore.DeleteBuilderArtifacts(current.Staging);
            return await _registry.PromoteAsync(
                    current.Staging,
                    current.State.MachineId,
                    current.State.OvmfCode.Path,
                    CreateBuildProvenance(current.State),
                    RegistrationFailureBehavior.PreserveStaging,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            await RestoreManifestAfterPromotionFailureAsync(current, exception).ConfigureAwait(false);
            throw new WindowsImageBuildCanceledException(
                "Windows image finalization was canceled; its staging area was preserved.",
                exception,
                current.Staging);
        }
        catch (Exception exception)
        {
            await RestoreManifestAfterPromotionFailureAsync(current, exception).ConfigureAwait(false);
            throw new WindowsImageBuildException(
                "Windows image finalization failed; its staging area was preserved.",
                exception,
                current.Staging);
        }
    }

    public async Task AbandonAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        using var operationLock = AcquireOperationLock(workspace.Staging);
        var current = await ReloadWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (current.State.Phase is WindowsImageBuildPhase.InstallerRunning or
            WindowsImageBuildPhase.VerificationRunning)
        {
            throw new WindowsImageBuildException(
                "An active or ambiguously interrupted image build must be recovered before it can be abandoned.",
                current.Staging);
        }

        ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(current.RuntimeDirectory);
        _registry.AbandonStaging(current.Staging);
    }

    private ImageBuildOperationLock AcquireOperationLock(ImageRegistrationStaging staging) =>
        ImageBuildOperationLock.Acquire(Path.Combine(_runtimeRoot, "locks"), staging.DirectoryPath);

    private async Task PrepareProvisioningMediaAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workspace.ProvisioningSourceDirectory);
        SetPrivateDirectoryMode(workspace.ProvisioningSourceDirectory);
        var executablePath = Path.Combine(
            workspace.ProvisioningSourceDirectory,
            GuestBridgeProvisioningContract.ExecutableFileName);
        File.Copy(workspace.State.GuestBridgeExecutable.Path, executablePath, overwrite: false);
        var actual = await ImageBuildFileHasher.HashAsync(
                executablePath,
                "staged GuestBridge executable",
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actual.Sha256,
                workspace.State.GuestBridgeExecutable.Sha256,
                StringComparison.Ordinal) ||
            actual.Length != workspace.State.GuestBridgeExecutable.Length)
        {
            throw new WindowsImageBuildException(
                "The GuestBridge executable changed while the provisioning media was staged.",
                workspace.Staging);
        }

        WritePrivateTextFile(
            Path.Combine(
                workspace.ProvisioningSourceDirectory,
                GuestBridgeProvisioningContract.InstallScriptFileName),
            CreateProvisioningPowerShell(workspace.State.GuestBridgeExecutable.Sha256));
        WritePrivateTextFile(
            Path.Combine(
                workspace.ProvisioningSourceDirectory,
                GuestBridgeProvisioningContract.AutounattendFileName),
            CreateAutounattend(
                CreateLocalAdministratorPassword(),
                workspace.State.Installation,
                workspace.State.DiskSizeGiB));
        WritePrivateTextFile(
            Path.Combine(
                workspace.ProvisioningSourceDirectory,
                GuestBridgeProvisioningContract.InstallCommandFileName),
            CreateProvisioningCommand());
        WritePrivateTextFile(
            Path.Combine(
                workspace.ProvisioningSourceDirectory,
                GuestBridgeProvisioningContract.ReadmeFileName),
            CreateProvisioningReadme());

        var result = await _processRunner.RunAsync(
                ImageBuilderCommandFactory.BuildProvisioningIso(workspace),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new WindowsImageBuildException(
                $"The isolated provisioning ISO build failed with exit code {result.ExitCode}: {Truncate(result.StandardError.Trim(), 1024)}",
                workspace.Staging);
        }

        var provisioningIso = ImageBuildPathGuard.RequireRegularFile(
            workspace.ProvisioningIsoPath,
            "GuestBridge provisioning ISO");
        if (new FileInfo(provisioningIso).Length <= 0)
        {
            throw new WindowsImageBuildException(
                "The GuestBridge provisioning ISO is empty.",
                workspace.Staging);
        }

        SetPrivateFileMode(provisioningIso);
    }

    private async Task<WindowsImageBuildWorkspace> ExecuteVmAsync(
        WindowsImageBuildWorkspace running,
        Func<WindowsImageBuildWorkspace, ProcessSpec> qemuFactory,
        WindowsImageBuildPhase retryPhase,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        IManagedProcess? swtpm = null;
        IManagedProcess? qemu = null;
        Exception? operationFailure = null;
        try
        {
            var swtpmSpec = ImageBuilderCommandFactory.ConfineSwtpm(
                running,
                ImageBuilderCommandFactory.BuildSwtpm(running));
            (running, swtpm) = await StartTrackedProcessAsync(
                    running,
                    WindowsImageBuildProcessNames.Swtpm,
                    swtpmSpec,
                    cancellationToken)
                .ConfigureAwait(false);
            await _endpointWaiter.WaitAsync(
                    running.SwtpmSocketPath,
                    swtpm,
                    EndpointTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var qemuSpec = ImageBuilderCommandFactory.ConfineQemu(
                running,
                qemuFactory(running));
            (running, qemu) = await StartTrackedProcessAsync(
                    running,
                    WindowsImageBuildProcessNames.Qemu,
                    qemuSpec,
                    cancellationToken)
                .ConfigureAwait(false);
            await _endpointWaiter.WaitAsync(
                    running.SpiceSocketPath,
                    qemu,
                    EndpointTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var exitCode = await qemu.WaitForExitAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new WindowsImageBuildException(
                    $"The Windows image-build QEMU process exited with code {exitCode}.",
                    running.Staging);
            }
        }
        catch (Exception exception)
        {
            operationFailure = exception is TimeoutException
                ? new WindowsImageBuildException(
                    $"The Windows virtual machine did not finish within {timeout.TotalMinutes:0} minutes.",
                    exception,
                    running.Staging)
                : exception;
            await TryQuitQemuAsync(running.QmpSocketPath, qemu).ConfigureAwait(false);
        }

        var cleanupFailures = await StopProcessesAsync(null, qemu, swtpm).ConfigureAwait(false);
        if (operationFailure is not null || cleanupFailures.Count > 0)
        {
            if (cleanupFailures.Count == 0 && operationFailure is not UnsafeProcessCleanupException)
            {
                var retry = running with
                {
                    State = CompleteRunningState(running.State, retryPhase),
                };
                try
                {
                    await WindowsImageBuildStateStore.WriteAsync(
                            retry.Staging,
                            retry.State,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception stateFailure)
                {
                    cleanupFailures.Add(stateFailure);
                }
            }

            ThrowInstallerFailure(operationFailure, cleanupFailures, running.Staging);
        }

        return running;
    }

    private async Task<(WindowsImageBuildWorkspace Workspace, IManagedProcess Process)> StartTrackedProcessAsync(
        WindowsImageBuildWorkspace workspace,
        string processName,
        ProcessSpec processSpec,
        CancellationToken cancellationToken)
    {
        var pending = workspace with
        {
            State = workspace.State with
            {
                PendingProcessName = processName,
                UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
            },
        };
        await WindowsImageBuildStateStore.WriteAsync(
                pending.Staging,
                pending.State,
                cancellationToken)
            .ConfigureAwait(false);

        var process = await _processLauncher.StartAsync(processSpec, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var identities = new Dictionary<string, ProcessIdentity>(
                pending.State.ActiveProcesses,
                StringComparer.Ordinal)
            {
                [processName] = process.Identity,
            };
            var tracked = pending with
            {
                State = pending.State with
                {
                    PendingProcessName = null,
                    ActiveProcesses = identities,
                    UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
                },
            };
            await WindowsImageBuildStateStore.WriteAsync(
                    tracked.Staging,
                    tracked.State,
                    cancellationToken)
                .ConfigureAwait(false);
            return (tracked, process);
        }
        catch (Exception stateFailure)
        {
            try
            {
                await StopProcessAsync(process).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new UnsafeProcessCleanupException(
                    $"The {processName} identity could not be persisted and the process could not be stopped.",
                    new AggregateException(stateFailure, cleanupFailure),
                    workspace.Staging);
            }

            throw new WindowsImageBuildException(
                $"The {processName} identity could not be persisted; the process was stopped.",
                stateFailure,
                workspace.Staging);
        }
    }

    private async Task TryQuitQemuAsync(string qmpSocketPath, IManagedProcess? qemu)
    {
        if (qemu is null || qemu.HasExited)
        {
            return;
        }

        try
        {
            await using var monitor = await _qmpConnector.ConnectAsync(
                    qmpSocketPath,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await monitor.QuitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or QmpException or TimeoutException or InvalidOperationException)
        {
            // Verified process termination below is the fallback when QMP is unavailable.
        }
    }

    private WindowsImageBuildState CreateRunningState(
        WindowsImageBuildState state,
        WindowsImageBuildPhase phase,
        string? verificationNonce)
    {
        if (phase is not (
                WindowsImageBuildPhase.InstallerRunning or
                WindowsImageBuildPhase.VerificationRunning))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        return state with
        {
            Phase = phase,
            ActiveHostBootId = _bootIdProvider.GetBootId(),
            PendingProcessName = null,
            ActiveProcesses = new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal),
            VerificationNonce = verificationNonce,
            VerifiedGuestEnvironment = null,
            UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
        };
    }

    private WindowsImageBuildState CompleteRunningState(
        WindowsImageBuildState state,
        WindowsImageBuildPhase phase,
        VerifiedGuestEnvironment? verifiedGuestEnvironment = null) =>
        state with
        {
            Phase = phase,
            ActiveHostBootId = null,
            PendingProcessName = null,
            ActiveProcesses = new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal),
            VerificationNonce = null,
            VerifiedGuestEnvironment = verifiedGuestEnvironment,
            UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
        };

    private async Task ValidateBaseImageAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var check = await _processRunner.RunAsync(
                ImageBuilderCommandFactory.ConfineQemuImg(
                    workspace,
                    ImageBuilderCommandFactory.BuildQemuImgCheck(workspace)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!check.IsSuccess)
        {
            throw new WindowsImageBuildException(
                $"qemu-img rejected the staged base image with exit code {check.ExitCode}: {Truncate(check.StandardError.Trim(), 1024)}",
                workspace.Staging);
        }

        var information = await _processRunner.RunAsync(
                ImageBuilderCommandFactory.ConfineQemuImg(
                    workspace,
                    ImageBuilderCommandFactory.BuildQemuImgInfo(workspace)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!information.IsSuccess || Encoding.UTF8.GetByteCount(information.StandardOutput) > 64 * 1024)
        {
            throw new WindowsImageBuildException(
                "qemu-img could not return bounded metadata for the staged base image.",
                workspace.Staging);
        }

        try
        {
            using var document = JsonDocument.Parse(
                information.StandardOutput,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("qemu-img metadata root is not an object.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException($"Duplicate qemu-img metadata property: {property.Name}");
                }
            }

            if (!document.RootElement.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.String ||
                !string.Equals(format.GetString(), "qcow2", StringComparison.Ordinal))
            {
                throw new JsonException("The staged base image is not qcow2.");
            }

            if (!document.RootElement.TryGetProperty("virtual-size", out var virtualSize) ||
                !virtualSize.TryGetInt64(out var virtualSizeBytes) ||
                virtualSizeBytes < 64L * 1024 * 1024 * 1024)
            {
                throw new JsonException("The staged base image virtual size is too small.");
            }

            foreach (var backingProperty in new[]
                     {
                         "backing-filename",
                         "full-backing-filename",
                         "backing-filename-format",
                     })
            {
                if (document.RootElement.TryGetProperty(backingProperty, out var backing) &&
                    backing.ValueKind is not JsonValueKind.Null)
                {
                    throw new JsonException("The sealed base image must not have a backing file.");
                }
            }
        }
        catch (JsonException exception)
        {
            throw new WindowsImageBuildException(
                "The staged base image metadata failed strict validation.",
                exception,
                workspace.Staging);
        }
    }

    private static void PrepareVerificationProbe(
        WindowsImageBuildWorkspace workspace,
        string nonce)
    {
        Directory.CreateDirectory(workspace.VerificationDirectory);
        SetPrivateDirectoryMode(workspace.VerificationDirectory);
        var path = Path.Combine(
            workspace.VerificationDirectory,
            GuestBridgeProvisioningContract.ProbeFileName);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", GuestBridgeProvisioningContract.SchemaVersion);
            writer.WriteString("nonce", nonce);
            writer.WriteString(
                "expectedGuestBridgeSha256",
                workspace.State.GuestBridgeExecutable.Sha256);
            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, buffer.ToArray());
        SetPrivateFileMode(path);
    }

    private VerifiedGuestEnvironment ValidateVerificationResult(WindowsImageBuildWorkspace workspace)
    {
        var resultPath = Path.Combine(
            workspace.VerificationDirectory,
            GuestBridgeProvisioningContract.ResultFileName);
        ImageBuildPathGuard.RequireNoSymbolicLinkComponents(
            resultPath,
            "GuestBridge verification result");
        var file = new FileInfo(resultPath);
        if (!file.Exists || file.Length <= 0 || file.Length > 16 * 1024)
        {
            throw new WindowsImageBuildException(
                "The GuestBridge verification result is missing or exceeds its size limit.",
                workspace.Staging);
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(resultPath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The verification result root must be an object.");
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    throw new JsonException($"Duplicate verification result property: {property.Name}");
                }
            }

            var expectedNames = new HashSet<string>(
                [
                    "schemaVersion",
                    "nonce",
                    "guestBridgeSha256",
                    "guestBridgeVersion",
                    "windowsArchitecture",
                    "windowsBuild",
                    "windowsEditionId",
                    "windowsDisplayVersion",
                ],
                StringComparer.Ordinal);
            if (properties.Count != expectedNames.Count ||
                properties.Keys.Any(name => !expectedNames.Contains(name)) ||
                !properties["schemaVersion"].TryGetInt32(out var schemaVersion) ||
                schemaVersion != GuestBridgeProvisioningContract.SchemaVersion ||
                properties["nonce"].ValueKind != JsonValueKind.String ||
                !string.Equals(
                    properties["nonce"].GetString(),
                    workspace.State.VerificationNonce,
                    StringComparison.Ordinal) ||
                properties["guestBridgeSha256"].ValueKind != JsonValueKind.String ||
                !string.Equals(
                    properties["guestBridgeSha256"].GetString(),
                    workspace.State.GuestBridgeExecutable.Sha256,
                    StringComparison.Ordinal) ||
                !TryReadBoundedJsonText(properties, "guestBridgeVersion", 128, out var guestBridgeVersion) ||
                !TryReadBoundedJsonText(properties, "windowsArchitecture", 16, out var windowsArchitecture) ||
                !string.Equals(windowsArchitecture, "X64", StringComparison.Ordinal) ||
                !properties["windowsBuild"].TryGetInt32(out var windowsBuild) ||
                windowsBuild < 22000 ||
                !TryReadBoundedJsonText(properties, "windowsEditionId", 128, out var windowsEditionId) ||
                !string.Equals(
                    windowsEditionId,
                    workspace.State.Installation.EditionId,
                    StringComparison.Ordinal) ||
                !TryReadBoundedJsonText(properties, "windowsDisplayVersion", 64, out var windowsDisplayVersion))
            {
                throw new JsonException("The verification result does not match the pinned probe.");
            }

            return new VerifiedGuestEnvironment(
                guestBridgeVersion,
                windowsArchitecture,
                windowsBuild,
                windowsEditionId,
                windowsDisplayVersion,
                _timeProvider.GetUtcNow().ToUniversalTime());
        }
        catch (JsonException exception)
        {
            throw new WindowsImageBuildException(
                "The GuestBridge verification result failed strict validation.",
                exception,
                workspace.Staging);
        }
    }

    private static bool TryReadBoundedJsonText(
        Dictionary<string, JsonElement> properties,
        string name,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        if (!properties.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString()!;
        return value.Length is > 0 &&
               value.Length <= maximumLength &&
               !value.Any(char.IsControl);
    }

    private async Task RecoverPersistedProcessesAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var currentBootId = _bootIdProvider.GetBootId();
        if (!string.Equals(
                workspace.State.ActiveHostBootId,
                currentBootId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (workspace.State.PendingProcessName is not null)
        {
            throw new WindowsImageBuildException(
                $"Recovery cannot prove whether pending process '{workspace.State.PendingProcessName}' started. Reboot the host before recovering this staging area.",
                workspace.Staging);
        }

        foreach (var processName in new[]
                 {
                     WindowsImageBuildProcessNames.Viewer,
                     WindowsImageBuildProcessNames.Qemu,
                     WindowsImageBuildProcessNames.Swtpm,
                 })
        {
            if (!workspace.State.ActiveProcesses.TryGetValue(processName, out var identity))
            {
                continue;
            }

            await RecoverProcessAsync(workspace, processName, identity, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecoverProcessAsync(
        WindowsImageBuildWorkspace workspace,
        string processName,
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        var status = await _processIdentityController.InspectAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (status == RecoveryProcessStatus.IdentityMismatch)
        {
            throw new WindowsImageBuildException(
                $"The persisted {processName} PID now belongs to a different process; no signal was sent.",
                workspace.Staging);
        }

        if (status == RecoveryProcessStatus.Stopped)
        {
            return;
        }

        if (processName == WindowsImageBuildProcessNames.Qemu)
        {
            try
            {
                await using var monitor = await _qmpConnector.ConnectAsync(
                        workspace.QmpSocketPath,
                        TimeSpan.FromSeconds(1),
                        cancellationToken)
                    .ConfigureAwait(false);
                await monitor.QuitAsync(cancellationToken).ConfigureAwait(false);
                status = await _processIdentityController.WaitForExitAsync(
                        identity,
                        ProcessStopTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (status == RecoveryProcessStatus.Stopped)
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or QmpException or TimeoutException or InvalidOperationException)
            {
                // Identity-checked signals below are the fallback.
            }
        }

        var signal = await _processIdentityController.SendTerminateAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (signal == RecoverySignalResult.IdentityMismatch)
        {
            throw new WindowsImageBuildException(
                $"The persisted {processName} identity changed before SIGTERM; no signal was sent.",
                workspace.Staging);
        }

        if (signal != RecoverySignalResult.Stopped)
        {
            status = await _processIdentityController.WaitForExitAsync(
                    identity,
                    ProcessStopTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (status == RecoveryProcessStatus.Stopped)
        {
            return;
        }

        if (status == RecoveryProcessStatus.IdentityMismatch)
        {
            throw new WindowsImageBuildException(
                $"The persisted {processName} identity changed while waiting for exit.",
                workspace.Staging);
        }

        signal = await _processIdentityController.SendKillAsync(identity, cancellationToken)
            .ConfigureAwait(false);
        if (signal == RecoverySignalResult.IdentityMismatch)
        {
            throw new WindowsImageBuildException(
                $"The persisted {processName} identity changed before SIGKILL; no signal was sent.",
                workspace.Staging);
        }

        status = signal == RecoverySignalResult.Stopped
            ? RecoveryProcessStatus.Stopped
            : await _processIdentityController.WaitForExitAsync(
                    identity,
                    ProcessStopTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        if (status != RecoveryProcessStatus.Stopped)
        {
            throw new WindowsImageBuildException(
                $"The persisted {processName} process could not be proven stopped; recovery was blocked.",
                workspace.Staging);
        }
    }

    private static string CreateProvisioningPowerShell(string guestBridgeSha256) =>
        $$"""
        #Requires -RunAsAdministrator
        $ErrorActionPreference = 'Stop'
        $source = Join-Path $PSScriptRoot '{{GuestBridgeProvisioningContract.ExecutableFileName}}'
        $expectedHash = '{{guestBridgeSha256}}'
        $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $expectedHash) { throw 'GuestBridge SHA-256 verification failed.' }

        $virtioRoot = Get-PSDrive -PSProvider FileSystem |
            ForEach-Object { Join-Path $_.Root 'vioscsi\w11\amd64\vioscsi.inf' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if (-not $virtioRoot) { throw 'The pinned virtio-win Windows 11 amd64 driver media was not found.' }
        $virtioDrive = [System.IO.Path]::GetPathRoot($virtioRoot)
        & pnputil.exe /add-driver (Join-Path $virtioDrive '*.inf') /subdirs /install
        if ($LASTEXITCODE -notin 0, 259, 3010) { throw "virtio driver installation failed: $LASTEXITCODE" }

        $destinationDirectory = Join-Path $env:ProgramFiles 'linuxcloth'
        $destination = Join-Path $destinationDirectory '{{GuestBridgeProvisioningContract.ExecutableFileName}}'
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $installedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($installedHash -cne $expectedHash) { throw 'Installed GuestBridge SHA-256 verification failed.' }

        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $action = New-ScheduledTaskAction -Execute $destination
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName 'linuxcloth GuestBridge' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

        $marker = Join-Path $env:ProgramData 'linuxcloth\guest-bridge-installed-v1'
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($marker)) -Force | Out-Null
        Set-Content -LiteralPath $marker -Value $expectedHash -Encoding ASCII
        $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Remove-ItemProperty -LiteralPath $winlogon -Name 'DefaultPassword','AutoAdminLogon','AutoLogonCount' -ErrorAction SilentlyContinue
        @(
          (Join-Path $env:WINDIR 'Panther\unattend.xml'),
          (Join-Path $env:WINDIR 'Panther\unattend-original.xml'),
          (Join-Path $env:WINDIR 'Panther\Unattend\unattend.xml')
        ) | ForEach-Object { Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue }
        Write-Host 'linuxcloth GuestBridge and Windows 11 virtio drivers were installed successfully.' -ForegroundColor Green
        Write-Host 'Windows will shut down to continue with the base-only verification boot.' -ForegroundColor Yellow
        & shutdown.exe /s /t 10 /c "linuxcloth provisioning completed"
        if ($LASTEXITCODE -ne 0) { throw "Windows shutdown request failed: $LASTEXITCODE" }
        """;

    private static string CreateLocalAdministratorPassword()
    {
        Span<byte> random = stackalloc byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        return $"Lc!{Convert.ToHexString(random)}";
    }

    internal static string CreateAutounattend(
        string password,
        WindowsInstallationSelection installation,
        int diskSizeGiB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidateInstallationSelection(installation);
        WindowsImageBuildStateStore.ValidateResources(diskSizeGiB, 2, 4096);
        var windowsPartitionSizeMiB = checked(diskSizeGiB * 1024 - 260 - 16 - 1536);
        return
        $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">
          <settings pass="windowsPE">
            <component name="Microsoft-Windows-PnpCustomizationsWinPE" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <DriverPaths>
                <PathAndCredentials wcm:action="add" wcm:keyValue="1">
                  <Path>E:\vioscsi\w11\amd64</Path>
                </PathAndCredentials>
              </DriverPaths>
            </component>
            <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <DiskConfiguration>
                <Disk wcm:action="add">
                  <DiskID>0</DiskID>
                  <WillWipeDisk>true</WillWipeDisk>
                  <CreatePartitions>
                    <CreatePartition wcm:action="add"><Order>1</Order><Type>EFI</Type><Size>260</Size></CreatePartition>
                    <CreatePartition wcm:action="add"><Order>2</Order><Type>MSR</Type><Size>16</Size></CreatePartition>
                    <CreatePartition wcm:action="add"><Order>3</Order><Type>Primary</Type><Size>{{windowsPartitionSizeMiB}}</Size></CreatePartition>
                    <CreatePartition wcm:action="add"><Order>4</Order><Type>Primary</Type><Extend>true</Extend></CreatePartition>
                  </CreatePartitions>
                  <ModifyPartitions>
                    <ModifyPartition wcm:action="add"><Order>1</Order><PartitionID>1</PartitionID><Format>FAT32</Format><Label>System</Label></ModifyPartition>
                    <ModifyPartition wcm:action="add"><Order>2</Order><PartitionID>2</PartitionID></ModifyPartition>
                    <ModifyPartition wcm:action="add"><Order>3</Order><PartitionID>3</PartitionID><Format>NTFS</Format><Label>Windows</Label><Letter>C</Letter></ModifyPartition>
                    <ModifyPartition wcm:action="add"><Order>4</Order><PartitionID>4</PartitionID><Format>NTFS</Format><Label>Recovery</Label><TypeID>de94bba4-06d1-4d40-a16a-bfd50179d6ac</TypeID></ModifyPartition>
                  </ModifyPartitions>
                </Disk>
                <WillShowUI>OnError</WillShowUI>
              </DiskConfiguration>
              <DynamicUpdate><Enable>false</Enable><WillShowUI>OnError</WillShowUI></DynamicUpdate>
              <ImageInstall>
                <OSImage>
                  <InstallFrom>
                    <MetaData wcm:action="add"><Key>/IMAGE/INDEX</Key><Value>{{installation.ImageIndex}}</Value></MetaData>
                  </InstallFrom>
                  <InstallTo><DiskID>0</DiskID><PartitionID>3</PartitionID></InstallTo>
                  <WillShowUI>OnError</WillShowUI>
                </OSImage>
              </ImageInstall>
              <UserData>
                <AcceptEula>true</AcceptEula>
                <FullName>linuxcloth</FullName>
                <Organization>linuxcloth</Organization>
              </UserData>
            </component>
          </settings>
          <settings pass="oobeSystem">
            <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
              <OOBE>
                <HideEULAPage>true</HideEULAPage>
                <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
                <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
                <ProtectYourPC>3</ProtectYourPC>
              </OOBE>
              <UserAccounts>
                <LocalAccounts>
                  <LocalAccount wcm:action="add">
                    <Password>
                      <Value>{{password}}</Value>
                      <PlainText>true</PlainText>
                    </Password>
                    <Description>Ephemeral linuxcloth Windows administrator</Description>
                    <DisplayName>linuxcloth</DisplayName>
                    <Group>Administrators</Group>
                    <Name>linuxcloth</Name>
                  </LocalAccount>
                </LocalAccounts>
              </UserAccounts>
              <AutoLogon>
                <Password>
                  <Value>{{password}}</Value>
                  <PlainText>true</PlainText>
                </Password>
                <Enabled>true</Enabled>
                <LogonCount>1</LogonCount>
                <Username>linuxcloth</Username>
              </AutoLogon>
              <FirstLogonCommands>
                <SynchronousCommand wcm:action="add">
                  <Order>1</Order>
                  <Description>Install the pinned linuxcloth GuestBridge and virtio drivers</Description>
                  <RequiresUserInput>false</RequiresUserInput>
                  <CommandLine>powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$script = Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root '{{GuestBridgeProvisioningContract.InstallScriptFileName}}' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1; if (-not $script) { throw 'linuxcloth provisioning media not found.' }; &amp; $script"</CommandLine>
                </SynchronousCommand>
              </FirstLogonCommands>
            </component>
          </settings>
        </unattend>
        """;
    }

    private static string CreateProvisioningCommand() =>
        """
        @echo off
        fltmc >nul 2>&1
        if not "%errorlevel%"=="0" (
          powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
          exit /b
        )
        powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LinuxCloth.ps1"
        echo.
        pause
        """;

    private static string CreateProvisioningReadme() =>
        """
        linuxcloth Windows 11 base image setup

        Windows Setup selects the approved edition, prepares the blank virtual disk, and loads
        the Windows 11 storage driver without user input. First sign-in creates a unique local
        linuxcloth administrator and continues without requiring a network connection.

        1. First sign-in automatically installs the pinned GuestBridge and virtio drivers,
           then shuts Windows down. If automatic provisioning reports an error, open the
           LINUXCLOTH CD and run Install-LinuxCloth.cmd manually.
        2. linuxcloth will boot the disk again without any installation media. Sign in once if asked.
           The pinned GuestBridge will answer the one-time probe and shut Windows down automatically.

        The image is not promoted unless that second-boot handshake succeeds.
        """;

    private static ManagedImageBuildProvenance CreateBuildProvenance(
        WindowsImageBuildState state)
    {
        var environment = state.VerifiedGuestEnvironment ??
                          throw new WindowsImageBuildException(
                              "Verified GuestBridge and Windows environment provenance is missing.");
        return new ManagedImageBuildProvenance(
            ToExternalMetadata(state.WindowsIso),
            ToExternalMetadata(state.VirtioWinIso),
            ToExternalMetadata(state.GuestBridgeExecutable),
            environment.GuestBridgeVersion,
            environment.WindowsArchitecture,
            environment.WindowsBuild,
            environment.WindowsEditionId,
            environment.WindowsDisplayVersion,
            state.DiskSizeGiB,
            state.CpuCount,
            state.MemoryMiB,
            ManagedImageBuildProvenance.GuestSelfReportEvidence,
            environment.VerifiedAt);
    }

    private static ExternalImageFileMetadata ToExternalMetadata(
        ImageBuildFileFingerprint fingerprint) =>
        new(
            fingerprint.Path,
            fingerprint.Sha256,
            fingerprint.Length,
            fingerprint.LastWriteUtcTicks);

    private static void WritePrivateTextFile(string path, string contents)
    {
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetPrivateFileMode(path);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class UnsafeProcessCleanupException : Exception
    {
        public UnsafeProcessCleanupException(
            string message,
            Exception innerException,
            ImageRegistrationStaging staging)
            : base(message, innerException)
        {
            Staging = staging;
        }

        public ImageRegistrationStaging Staging { get; }
    }

    private async Task<WindowsImageBuildWorkspace> CompletePreparationAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken)
    {
        if (workspace.State.Phase != WindowsImageBuildPhase.Preparing)
        {
            throw new WindowsImageBuildException("Only a preparing staging area can be prepared.", workspace.Staging);
        }

        DeleteFixedFileIfPresent(workspace.Staging.BaseImagePath, "partial base image");
        DeleteFixedFileIfPresent(
            workspace.Staging.OvmfVariablesTemplatePath,
            "partial OVMF variables template");

        var result = await _processRunner.RunAsync(
                ImageBuilderCommandFactory.ConfineQemuImg(
                    workspace,
                    ImageBuilderCommandFactory.BuildQemuImgCreate(workspace)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new WindowsImageBuildException(
                $"qemu-img failed with exit code {result.ExitCode}: {Truncate(result.StandardError.Trim(), 1024)}",
                workspace.Staging);
        }

        var baseImage = ImageBuildPathGuard.RequireRegularFile(
            workspace.Staging.BaseImagePath,
            "new sparse base image");
        if (new FileInfo(baseImage).Length <= 0)
        {
            throw new WindowsImageBuildException("qemu-img created an empty base image.", workspace.Staging);
        }

        File.Copy(
            workspace.State.OvmfVariablesSource.Path,
            workspace.Staging.OvmfVariablesTemplatePath,
            overwrite: false);
        SetPrivateFileMode(workspace.Staging.BaseImagePath);
        SetPrivateFileMode(workspace.Staging.OvmfVariablesTemplatePath);

        var prepared = workspace with { State = WithPhase(workspace.State, WindowsImageBuildPhase.Prepared) };
        await WindowsImageBuildStateStore.WriteAsync(
                prepared.Staging,
                prepared.State,
                cancellationToken)
            .ConfigureAwait(false);
        return prepared;
    }

    private WindowsImageBuildRequest NormalizeAndValidateRequest(WindowsImageBuildRequest request)
    {
        _ = request.ImageId.Value;
        ArgumentNullException.ThrowIfNull(request.Toolchain);
        WindowsImageBuildStateStore.ValidateResources(
            request.DiskSizeGiB,
            request.CpuCount,
            request.MemoryMiB);

        var windowsIso = ImageBuildPathGuard.RequireRegularFile(
            request.WindowsIsoPath,
            "Windows installation ISO");
        var virtioWinIso = ImageBuildPathGuard.RequireRegularFile(
            request.VirtioWinIsoPath,
            "virtio-win ISO");
        var guestBridge = ImageBuildPathGuard.RequireRegularFile(
            request.GuestBridgeExecutablePath,
            "GuestBridge executable");
        var ovmfCode = ImageBuildPathGuard.RequireRegularFile(
            request.OvmfCodePath,
            "OVMF code image");
        var ovmfVariables = ImageBuildPathGuard.RequireRegularFile(
            request.OvmfVariablesTemplatePath,
            "OVMF variables template");
        var toolchain = NormalizeToolchain(request.Toolchain);
        var installation = request.Installation ??
                           throw new WindowsImageBuildException(
                               "A reviewed Windows installation image selection is required.");
        ValidateInstallationSelection(installation);

        var resources = new[] { windowsIso, virtioWinIso, guestBridge, ovmfCode, ovmfVariables };
        if (resources.Distinct(StringComparer.Ordinal).Count() != resources.Length)
        {
            throw new WindowsImageBuildException("Installation media and firmware resources must be distinct files.");
        }

        foreach (var resource in resources)
        {
            if (IsSameOrDescendant(resource, _registry.ImagesDirectory))
            {
                throw new WindowsImageBuildException(
                    "External installation media and firmware cannot be stored inside the managed image registry.");
            }
        }

        return request with
        {
            WindowsIsoPath = windowsIso,
            VirtioWinIsoPath = virtioWinIso,
            GuestBridgeExecutablePath = guestBridge,
            OvmfCodePath = ovmfCode,
            OvmfVariablesTemplatePath = ovmfVariables,
            Toolchain = toolchain,
            Installation = installation,
        };
    }

    private static void ValidateInstallationSelection(WindowsInstallationSelection installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.ImageIndex <= 0 ||
            string.IsNullOrWhiteSpace(installation.EditionId) ||
            installation.EditionId.Length > 128 ||
            installation.EditionId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(installation.DisplayName) ||
            installation.DisplayName.Length > 256 ||
            installation.DisplayName.Any(char.IsControl))
        {
            throw new WindowsImageBuildException("The Windows installation image selection is invalid.");
        }
    }

    private static WindowsImageBuildToolchain NormalizeToolchain(WindowsImageBuildToolchain toolchain) =>
        new(
            ImageBuildPathGuard.RequireRegularFile(toolchain.QemuSystem, "QEMU executable", true),
            ImageBuildPathGuard.RequireRegularFile(toolchain.QemuImg, "qemu-img executable", true),
            ImageBuildPathGuard.RequireRegularFile(toolchain.Swtpm, "swtpm executable", true),
            ImageBuildPathGuard.RequireRegularFile(toolchain.RemoteViewer, "remote-viewer executable", true),
            ImageBuildPathGuard.RequireRegularFile(toolchain.Xorriso, "xorriso executable", true),
            ImageBuildPathGuard.RequireRegularFile(toolchain.Bubblewrap, "Bubblewrap executable", true));

    private async Task ValidateDependenciesAsync(
        WindowsImageBuildState state,
        bool includeMedia,
        bool includeVariables,
        bool includeGuestBridge,
        CancellationToken cancellationToken)
    {
        if (includeMedia)
        {
            var media = await _mediaValidator.ValidateAsync(
                    state.WindowsIso.Path,
                    state.VirtioWinIso.Path,
                    state.Toolchain.Xorriso,
                    state.Toolchain.Bubblewrap,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireMatchingFingerprint(state.WindowsIso, media.WindowsIso, "Windows installation ISO");
            RequireMatchingFingerprint(state.VirtioWinIso, media.VirtioWinIso, "virtio-win ISO");
            _ = NormalizeToolchain(state.Toolchain);
        }

        var ovmfCode = await ImageBuildFileHasher.HashAsync(
                state.OvmfCode.Path,
                "OVMF code image",
                cancellationToken)
            .ConfigureAwait(false);
        RequireMatchingFingerprint(state.OvmfCode, ovmfCode, "OVMF code image");

        if (includeVariables)
        {
            var variables = await ImageBuildFileHasher.HashAsync(
                    state.OvmfVariablesSource.Path,
                    "OVMF variables template",
                    cancellationToken)
                .ConfigureAwait(false);
            RequireMatchingFingerprint(
                state.OvmfVariablesSource,
                variables,
                "OVMF variables template");
        }


        if (includeGuestBridge)
        {
            var guestBridge = await ImageBuildFileHasher.HashAsync(
                    state.GuestBridgeExecutable.Path,
                    "GuestBridge executable",
                    cancellationToken)
                .ConfigureAwait(false);
            RequireMatchingFingerprint(
                state.GuestBridgeExecutable,
                guestBridge,
                "GuestBridge executable");
        }
    }

    private async Task<WindowsImageBuildWorkspace> ReloadWorkspaceAsync(
        WindowsImageBuildWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var staging = _registry.OpenStaging(
            workspace.Staging.ImageId,
            workspace.Staging.DirectoryPath);
        var state = await WindowsImageBuildStateStore.ReadAsync(staging, cancellationToken)
            .ConfigureAwait(false);
        return CreateWorkspace(staging, state);
    }

    private static async Task RestoreManifestAfterPromotionFailureAsync(
        WindowsImageBuildWorkspace workspace,
        Exception promotionFailure)
    {
        if (Directory.Exists(workspace.Staging.DirectoryPath))
        {
            try
            {
                await WindowsImageBuildStateStore.WriteAsync(
                        workspace.Staging,
                        workspace.State,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception restoreFailure)
            {
                throw new WindowsImageBuildException(
                    "Image promotion failed and its resumable state manifest could not be restored.",
                    new AggregateException(promotionFailure, restoreFailure),
                    workspace.Staging);
            }
        }
    }

    private WindowsImageBuildState WithPhase(
        WindowsImageBuildState state,
        WindowsImageBuildPhase phase) =>
        state with
        {
            Phase = phase,
            UpdatedAt = _timeProvider.GetUtcNow().ToUniversalTime(),
        };

    private WindowsImageBuildWorkspace CreateWorkspace(
        ImageRegistrationStaging staging,
        WindowsImageBuildState state) =>
        new(
            staging,
            state,
            Path.Combine(_runtimeRoot, state.MachineId.ToString("N")[..12]));

    private static void ValidatePreparedArtifacts(ImageRegistrationStaging staging)
    {
        ImageBuildPathGuard.RequireRegularFile(staging.BaseImagePath, "staged base image");
        ImageBuildPathGuard.RequireRegularFile(
            staging.OvmfVariablesTemplatePath,
            "staged OVMF variables template");
        ImageBuildPathGuard.RequireNoSymbolicLinkComponents(
            staging.SwtpmStateTemplateDirectory,
            "swtpm state template");
        if (!Directory.Exists(staging.SwtpmStateTemplateDirectory))
        {
            throw new WindowsImageBuildException("The swtpm state template directory is missing.", staging);
        }

        ValidateTpmTree(staging, requireFile: false);
    }

    private static void PrepareRuntimeDirectory(WindowsImageBuildWorkspace workspace)
    {
        ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(workspace.RuntimeDirectory);
        Directory.CreateDirectory(workspace.RuntimeDirectory);
        Directory.CreateDirectory(workspace.SocketsDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                workspace.RuntimeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                workspace.SocketsDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void EnsureTpmWasInitialized(ImageRegistrationStaging staging) =>
        ValidateTpmTree(staging, requireFile: true);

    private static void ValidateTpmTree(
        ImageRegistrationStaging staging,
        bool requireFile)
    {
        var hasRegularFile = false;
        var entryCount = 0;
        var pending = new Stack<string>();
        pending.Push(staging.SwtpmStateTemplateDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > ImageRegistryLimits.MaximumTpmEntryCount)
                {
                    throw new WindowsImageBuildException(
                        "The swtpm state template exceeds its entry limit.",
                        staging);
                }

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new WindowsImageBuildException(
                        "The swtpm template cannot contain symbolic links or reparse points.",
                        staging);
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
                else
                {
                    hasRegularFile = true;
                }
            }
        }

        if (requireFile && !hasRegularFile)
        {
            throw new WindowsImageBuildException(
                "The installer exited without initializing the TPM 2.0 state template.",
                staging);
        }
    }

    private static async Task<List<Exception>> StopProcessesAsync(
        IManagedProcess? viewer,
        IManagedProcess? qemu,
        IManagedProcess? swtpm)
    {
        var failures = new List<Exception>();
        foreach (var process in new[] { viewer, qemu, swtpm })
        {
            if (process is null)
            {
                continue;
            }

            try
            {
                await StopProcessAsync(process).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static async Task StopProcessAsync(IManagedProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _ = await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(ProcessStopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await process.KillAsync(CancellationToken.None).ConfigureAwait(false);
                    _ = await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(ProcessStopTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ThrowInstallerFailure(
        Exception? operationFailure,
        List<Exception> cleanupFailures,
        ImageRegistrationStaging staging)
    {
        if (operationFailure is OperationCanceledException canceled && cleanupFailures.Count == 0)
        {
            throw new WindowsImageBuildCanceledException(
                "The Windows installer was canceled; its staging area was preserved.",
                canceled,
                staging);
        }

        if (operationFailure is not null)
        {
            cleanupFailures.Insert(0, operationFailure);
        }

        if (cleanupFailures.Count == 1)
        {
            var failure = cleanupFailures[0];
            if (failure is WindowsImageBuildException buildFailure && buildFailure.Staging is not null)
            {
                ExceptionDispatchInfo.Capture(buildFailure).Throw();
            }

            throw new WindowsImageBuildException(
                "The Windows installer failed; its staging area was preserved.",
                failure,
                staging);
        }

        throw new WindowsImageBuildException(
            "The Windows installer and one or more cleanup operations failed; its staging area was preserved.",
            new AggregateException(cleanupFailures),
            staging);
    }

    private static void RequireMatchingFingerprint(
        ImageBuildFileFingerprint expected,
        ImageBuildFileFingerprint actual,
        string description)
    {
        if (!string.Equals(expected.Path, actual.Path, StringComparison.Ordinal) ||
            !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal) ||
            expected.Length != actual.Length ||
            expected.LastWriteUtcTicks != actual.LastWriteUtcTicks)
        {
            throw new WindowsImageBuildException(
                $"The {description} changed after this image build was created.");
        }
    }

    private static void DeleteFixedFileIfPresent(string path, string description)
    {
        if (!File.Exists(path))
        {
            return;
        }

        ImageBuildPathGuard.RequireRegularFile(path, description);
        File.Delete(path);
    }

    private static void ValidateSocketPaths(WindowsImageBuildWorkspace workspace)
    {
        foreach (var socket in new[]
                 {
                     workspace.SwtpmSocketPath,
                     workspace.SpiceSocketPath,
                     workspace.QmpSocketPath,
                 })
        {
            if (Encoding.UTF8.GetByteCount(socket) > 100)
            {
                throw new PathTooLongException(
                    $"The image staging path is too long for a safe Unix socket: {socket}");
            }
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathFullyQualified(relative));
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static string EnsureRuntimeRoot(string path)
    {
        var fullPath = ImageBuildPathGuard.NormalizeAbsolute(path, "image-builder runtime root");
        ImageBuildPathGuard.RequireNoSymbolicLinkComponents(fullPath, "image-builder runtime root");
        if (File.Exists(fullPath))
        {
            throw new WindowsImageBuildException(
                $"A file exists where the image-builder runtime root is required: {fullPath}");
        }

        Directory.CreateDirectory(fullPath);
        ImageBuildPathGuard.RequireNoSymbolicLinkComponents(fullPath, "image-builder runtime root");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return fullPath;
    }

    private static string GetDefaultRuntimeRoot()
    {
        var xdgRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(xdgRuntime) && Path.IsPathFullyQualified(xdgRuntime))
        {
            return Path.Combine(xdgRuntime, "linuxcloth", "image-build");
        }

        var userBytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Environment.UserName));
        var userSuffix = Convert.ToHexString(userBytes.AsSpan(0, 4)).ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), $"linuxcloth-image-build-{userSuffix}");
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
