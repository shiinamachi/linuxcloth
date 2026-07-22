using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Setup;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;
using LinuxCloth.Desktop.ViewModels;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Tests;

public sealed class SetupWizardViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcw{Guid.NewGuid():N}"[..9]);

    [Fact]
    public async Task OnePreparationActionIsGatedByValidatedInputsAndLicense()
    {
        var runtime = new FakeSetupService();
        await using var viewModel = CreateViewModel(runtime, new FakeRunStore());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsReady);
        Assert.False(viewModel.CanPrepare);
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        Assert.False(viewModel.CanPrepare);
        viewModel.IsLicenseConfirmed = true;
        Assert.True(viewModel.CanPrepare);
        Assert.Equal("자동 준비", viewModel.VirtioMediaName);
    }

    [Fact]
    public async Task MultipleWindowsEditionsRequireOneExplicitLinuxSideSelection()
    {
        var runtime = new FakeSetupService { MultipleWindowsEditions = true };
        await using var viewModel = CreateViewModel(runtime, new FakeRunStore());
        await viewModel.InitializeAsync();

        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        Assert.True(viewModel.HasMultipleWindowsEditions);
        Assert.Null(viewModel.SelectedWindowsEdition);
        Assert.False(viewModel.CanPrepare);
        viewModel.SelectedWindowsEdition = viewModel.WindowsEditions.Single(image => image.Index == 6);
        Assert.True(viewModel.CanPrepare);
    }

    [Fact]
    public async Task PrepareRunsThroughImageRegistrationAndCompletesDurableRun()
    {
        var runtime = new FakeSetupService();
        var runStore = new FakeRunStore();
        await using var viewModel = CreateViewModel(runtime, runStore);
        await viewModel.InitializeAsync();
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;
        var completed = 0;
        viewModel.Completed += (_, _) => completed++;

        await viewModel.PrepareAsync();

        Assert.Equal(1, runtime.BuildCount);
        Assert.Equal(1, runtime.PinnedVirtioPrepareCount);
        Assert.Equal("/cache/virtio-win.iso", runtime.LastBuildRequest?.VirtioWinIsoPath);
        Assert.Equal(1, completed);
        Assert.False(viewModel.IsRunning);
        Assert.Equal(SetupPhase.Completed, runStore.Run?.Phase);
        Assert.Null(runStore.Run?.Inputs.WindowsIsoPath);
        Assert.All(viewModel.Phases, phase => Assert.True(phase.IsComplete));
    }

    [Fact]
    public async Task ActiveDurableRunResumesAutomaticallyOnInitialization()
    {
        var runtime = new FakeSetupService();
        var now = DateTimeOffset.UtcNow;
        var inputs = CreateInputs();
        var runStore = new FakeRunStore
        {
            Run = new SetupRun(
                SetupRun.CurrentSchemaVersion,
                Guid.NewGuid(),
                SetupPhase.BuildingImage,
                inputs,
                "/data/images/.staging-windows-11",
                null,
                2,
                now,
                now),
        };
        await using var viewModel = CreateViewModel(runtime, runStore);

        await viewModel.InitializeAsync();

        Assert.Equal(1, runtime.ResumeCount);
        Assert.Equal(SetupPhase.Completed, runStore.Run?.Phase);
    }

    [Fact]
    public async Task ViewerOpensOnlyOnExplicitActionDuringLiveBuild()
    {
        var runtime = new FakeSetupService { WaitDuringInstall = true };
        await using var viewModel = CreateViewModel(runtime, new FakeRunStore());
        await viewModel.InitializeAsync();
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        var preparation = viewModel.PrepareAsync();
        await runtime.BuildDisplayStarted.Task;
        await WaitUntilAsync(() => viewModel.CanViewInstaller);
        Assert.Equal(0, runtime.ViewerCount);

        await viewModel.ViewInstallerAsync();

        Assert.Equal(1, runtime.ViewerCount);
        Assert.Equal("/data/images/.staging-windows-11", runtime.ViewerStagingDirectory);
        runtime.ContinueBuild.TrySetResult();
        await preparation;
        Assert.False(viewModel.CanViewInstaller);
    }

    [Fact]
    public async Task PackageKitAbsenceProducesOneManualRecoveryAction()
    {
        var runtime = new FakeSetupService { DependenciesReady = false };
        var runStore = new FakeRunStore();
        await using var viewModel = CreateViewModel(runtime, runStore);
        await viewModel.InitializeAsync();
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        Assert.True(viewModel.CanPrepare);
        await viewModel.PrepareAsync();

        Assert.True(
            viewModel.IsBlocked,
            $"phase={runStore.Run?.Phase}; code={runStore.Run?.Blocker?.Code}; error={viewModel.ErrorMessage}");
        Assert.True(viewModel.ShowManualCommand);
        Assert.StartsWith("sudo apt install -- ", viewModel.ManualInstallCommand, StringComparison.Ordinal);
        Assert.Equal("SETUP-PKG-MANUAL", viewModel.BlockerCode);
        Assert.Equal(0, runtime.BuildCount);
    }

    [Fact]
    public async Task UnknownDistributionDoesNotBlockSatisfiedCapabilities()
    {
        var runtime = new FakeSetupService();
        var runStore = new FakeRunStore();
        await using var viewModel = CreateViewModel(
            runtime,
            runStore,
            "ID=opensuse\nNAME=openSUSE\n");
        await viewModel.InitializeAsync();
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        await viewModel.PrepareAsync();

        Assert.Equal(1, runtime.BuildCount);
        Assert.False(viewModel.IsBlocked);
        Assert.Equal(SetupPhase.Completed, runStore.Run?.Phase);
    }

    [Fact]
    public async Task UnknownDistributionReportsMissingCapabilitiesInsteadOfItsName()
    {
        var runtime = new FakeSetupService { DependenciesReady = false };
        var runStore = new FakeRunStore();
        await using var viewModel = CreateViewModel(
            runtime,
            runStore,
            "ID=opensuse\nNAME=openSUSE\n");
        await viewModel.InitializeAsync();
        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        await viewModel.PrepareAsync();

        Assert.True(viewModel.IsBlocked);
        Assert.Equal("SETUP-CAPABILITY-MANUAL", viewModel.BlockerCode);
        Assert.Equal("필수 구성 요소를 직접 준비해야 합니다", viewModel.BlockerTitle);
        Assert.DoesNotContain("배포판", viewModel.BlockerDescription, StringComparison.Ordinal);
        Assert.Contains(QemuDoctorCheckCodes.Firmware, viewModel.TechnicalDetail, StringComparison.Ordinal);
        Assert.False(viewModel.ShowManualCommand);
        Assert.Equal(0, runtime.BuildCount);
    }

    [Fact]
    public async Task DisposalCancelsAndWaitsForActiveMediaAnalysis()
    {
        var runtime = new FakeSetupService { WaitForMediaCancellation = true };
        var viewModel = CreateViewModel(runtime, new FakeRunStore());
        await viewModel.InitializeAsync();
        var validation = viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        await runtime.MediaValidationStarted.Task;

        await viewModel.DisposeAsync();
        await validation;

        Assert.True(runtime.MediaCancellationObserved);
        Assert.False(viewModel.HasActiveOperation);
    }

    private SetupWizardViewModel CreateViewModel(
        FakeSetupService runtime,
        ISetupRunStore runStore,
        string osReleaseContents = "ID=debian\nNAME=Debian\nVERSION_ID=12\n")
    {
        Directory.CreateDirectory(_root);
        var osRelease = Path.Combine(_root, "os-release");
        File.WriteAllText(osRelease, osReleaseContents);
        return new SetupWizardViewModel(
            runtime,
            CreateFirstRun(runtime.DependenciesReady),
            new FakeStateStore(SetupState.Default),
            runStore,
            new DistributionInfoReader(osRelease),
            new PackagePlanResolver(new FakeManifestSource()),
            new ManualOnlyPackageInstaller(),
            HostCapacityInspector.Evaluate(
                16L * 1024 * 1024 * 1024,
                128L * 1024 * 1024 * 1024,
                8));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static FirstRunSnapshot CreateFirstRun(bool dependenciesReady)
    {
        var doctor = FakeSetupService.CreateDoctor(dependenciesReady);
        var startup = new DesktopStartupSnapshot(
            null!,
            [],
            [],
            doctor,
            [],
            new DesktopImageBuildDefaults(
                "/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe",
                true,
                "/usr/share/OVMF_CODE.fd",
                "/usr/share/OVMF_VARS.fd"),
            [],
            []);
        return new FirstRunSnapshot(startup, SetupReadinessEvaluator.Evaluate(startup));
    }

    private static SetupInputSnapshot CreateInputs() => new(
        "/media/windows.iso",
        new SetupFileFingerprint(1024, DateTimeOffset.UtcNow, new string('a', 64)),
        "/media/virtio.iso",
        new SetupFileFingerprint(1024, DateTimeOffset.UtcNow, new string('b', 64)),
        6,
        "Professional",
        "Windows 11 Pro",
        null,
        ImageId.Parse("windows-11"),
        96,
        4,
        6144,
        true);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeManifestSource : IPackageManifestSource
    {
        public Task<string> ReadAsync(
            DistributionFamily family,
            bool imageBuild,
            CancellationToken cancellationToken = default)
        {
            _ = family;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(imageBuild ? "xorriso\n" : "qemu-system-x86\n");
        }
    }

    private sealed class ManualOnlyPackageInstaller : IPackageInstaller
    {
        public Task<PackageInstallPreview> ResolveAsync(
            PackagePlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PackageInstallPreview(plan, false, [], [], []));
        }

        public Task<PackageInstallResult> InstallAsync(
            PackageInstallPreview preview,
            IProgress<PackageInstallProgress> progress,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("manual-only installer");
    }

    private sealed class FakeStateStore(SetupState state) : ISetupStateStore
    {
        private SetupState _state = state;

        public Task<SetupState> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_state);
        }

        public Task SaveAsync(SetupState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunStore : ISetupRunStore
    {
        public SetupRun? Run { get; set; }

        public Task<SetupRun?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Run);
        }

        public Task SaveAsync(SetupRun run, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run = run;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSetupService : IDesktopSetupService
    {
        private bool _verified;

        public bool DependenciesReady { get; init; } = true;

        public bool MultipleWindowsEditions { get; init; }

        public bool WaitForMediaCancellation { get; init; }

        public bool WaitDuringInstall { get; init; }

        public bool MediaCancellationObserved { get; private set; }

        public TaskCompletionSource MediaValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BuildDisplayStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueBuild { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BuildCount { get; private set; }

        public int ResumeCount { get; private set; }

        public int PinnedVirtioPrepareCount { get; private set; }

        public int ViewerCount { get; private set; }

        public string? ViewerStagingDirectory { get; private set; }

        public DesktopImageBuildRequest? LastBuildRequest { get; private set; }

        public Task<QemuDoctorResult> InspectHostAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDoctor(DependenciesReady));
        }

        public Task<ImageBuildFileFingerprint?> FindCachedVirtioMediaAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ImageBuildFileFingerprint?>(null);
        }

        public Task<ImageBuildFileFingerprint> PreparePinnedVirtioMediaAsync(
            IProgress<VirtioMediaDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PinnedVirtioPrepareCount++;
            var fingerprint = Fingerprint("/cache/virtio-win.iso", 'b');
            progress?.Report(new VirtioMediaDownloadProgress("ready", fingerprint.Length, fingerprint.Length));
            return Task.FromResult(fingerprint);
        }

        public Task ViewImageBuildAsync(
            ImageId imageId,
            string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            _ = imageId;
            cancellationToken.ThrowIfCancellationRequested();
            ViewerCount++;
            ViewerStagingDirectory = stagingDirectory;
            return Task.CompletedTask;
        }

        public Task<bool> HasVerifiedImageAsync(
            ImageId imageId,
            CancellationToken cancellationToken = default)
        {
            _ = imageId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_verified);
        }

        public Task<(
            IReadOnlyList<ResumableImageBuild> Builds,
            IReadOnlyList<ImageBuildRecoveryIssue> Issues)> FindResumableBuildsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<(
                IReadOnlyList<ResumableImageBuild>,
                IReadOnlyList<ImageBuildRecoveryIssue>)>(([], []));
        }

        public Task<DesktopImageBuildDefaults> GetImageBuildDefaultsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new DesktopImageBuildDefaults(
                    "/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe",
                    true,
                    "/usr/share/OVMF_CODE.fd",
                    "/usr/share/OVMF_VARS.fd"));

        public Task<ImageBuildFileFingerprint> ValidateWindowsMediaAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Fingerprint(path, 'a'));

        public Task<DesktopWindowsMediaAnalysis> AnalyzeWindowsMediaAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            if (WaitForMediaCancellation)
            {
                MediaValidationStarted.TrySetResult();
                return WaitForAnalysisCancellationAsync(cancellationToken);
            }

            WindowsInstallationImage[] images = MultipleWindowsEditions
                ?
                [
                    new WindowsInstallationImage(1, "Windows 11 Home", "Core", "amd64", 26100),
                    new WindowsInstallationImage(6, "Windows 11 Pro", "Professional", "amd64", 26100),
                ]
                : [new WindowsInstallationImage(6, "Windows 11 Pro", "Professional", "amd64", 26100)];
            return Task.FromResult(
                new DesktopWindowsMediaAnalysis(
                    Fingerprint(path, 'a'),
                    new WindowsInstallationCatalog(images, MultipleWindowsEditions ? null : 6)));
        }

        public Task<ImageBuildFileFingerprint> ValidateVirtioMediaAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Fingerprint(path, 'b'));

        public async Task<ManagedWindowsImage> BuildImageAsync(
            DesktopImageBuildRequest request,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCount++;
            LastBuildRequest = request;
            progress.Report(
                new DesktopImageBuildProgress(
                    WindowsImageBuildPhase.InstallerRunning,
                    "/data/images/.staging-windows-11"));
            if (WaitDuringInstall)
            {
                BuildDisplayStarted.TrySetResult();
                await ContinueBuild.Task.WaitAsync(cancellationToken);
            }

            progress.Report(
                new DesktopImageBuildProgress(
                    WindowsImageBuildPhase.VerificationRunning,
                    "/data/images/.staging-windows-11"));
            _verified = true;
            return CreateImage(request.ImageId);
        }

        public Task<ManagedWindowsImage> ResumeImageBuildAsync(
            ImageId imageId,
            string stagingDirectory,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default)
        {
            _ = stagingDirectory;
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCount++;
            progress.Report(
                new DesktopImageBuildProgress(
                    WindowsImageBuildPhase.VerificationRunning,
                    stagingDirectory));
            _verified = true;
            return Task.FromResult(CreateImage(imageId));
        }

        public static QemuDoctorResult CreateDoctor(bool dependenciesReady)
        {
            string[] codes =
            [
                QemuDoctorCheckCodes.Platform,
                QemuDoctorCheckCodes.Kvm,
                QemuDoctorCheckCodes.QemuSystem,
                QemuDoctorCheckCodes.QemuImg,
                QemuDoctorCheckCodes.Swtpm,
                QemuDoctorCheckCodes.RemoteViewer,
                QemuDoctorCheckCodes.Bubblewrap,
                QemuDoctorCheckCodes.WimlibImagex,
                QemuDoctorCheckCodes.SevenZip,
                QemuDoctorCheckCodes.Xorriso,
                QemuDoctorCheckCodes.Firmware,
                QemuDoctorCheckCodes.RuntimeDirectory,
            ];
            var checks = codes.Select(code => new DoctorCheck(
                code,
                true,
                code is QemuDoctorCheckCodes.Platform or QemuDoctorCheckCodes.Kvm || dependenciesReady,
                "ready")).ToArray();
            return new QemuDoctorResult(new DoctorReport(checks), null, null, null);
        }

        private static ImageBuildFileFingerprint Fingerprint(string path, char hashCharacter) =>
            new(Path.GetFullPath(path), new string(hashCharacter, 64), 1024, 1);

        private async Task<DesktopWindowsMediaAnalysis> WaitForAnalysisCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("미디어 분석이 취소되지 않았습니다.");
            }
            catch (OperationCanceledException)
            {
                MediaCancellationObserved = true;
                throw;
            }
        }

        private static ManagedWindowsImage CreateImage(ImageId imageId)
        {
            var metadata = new ManagedImageMetadata(
                ManagedImageMetadata.CurrentSchemaVersion,
                imageId,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new ManagedImageFileMetadata("base", 1, 1),
                new ExternalImageFileMetadata("/firmware/code.fd", "code", 1, 1),
                new ManagedImageFileMetadata("vars", 1, 1),
                new ManagedImageTreeMetadata("tpm", 1, 1, 1),
                null);
            return new ManagedWindowsImage(
                metadata,
                "/images/test",
                "/images/test/base.qcow2",
                "/images/test/ovmf-vars.template.fd",
                "/images/test/swtpm-state.template");
        }
    }
}
