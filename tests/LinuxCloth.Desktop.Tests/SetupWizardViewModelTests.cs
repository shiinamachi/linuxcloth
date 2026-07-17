using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;
using LinuxCloth.Desktop.ViewModels;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Tests;

public sealed class SetupWizardViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcw{Guid.NewGuid():N}"[..9]);

    [Fact]
    public async Task GatesMediaStepsOnImmediateValidationAndLicenseConfirmation()
    {
        var runtime = new FakeSetupService();
        var state = new FakeStateStore(SetupState.Default);
        await using var viewModel = CreateViewModel(runtime, state, CreateFirstRun());
        await viewModel.InitializeAsync();

        await viewModel.ContinueAsync();
        Assert.Equal(SetupStep.Components, viewModel.CurrentStep);
        Assert.True(viewModel.CanContinue);

        await viewModel.ContinueAsync();
        Assert.Equal(SetupStep.WindowsMedia, viewModel.CurrentStep);
        Assert.False(viewModel.CanContinue);

        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        Assert.True(viewModel.IsWindowsMediaValid);
        Assert.False(viewModel.CanContinue);
        viewModel.IsLicenseConfirmed = true;
        Assert.True(viewModel.CanContinue);

        await viewModel.ContinueAsync();
        await viewModel.ValidateVirtioMediaAsync("/media/virtio.iso");
        Assert.Equal(1, runtime.WindowsValidationCount);
        Assert.Equal(1, runtime.VirtioValidationCount);
        Assert.True(viewModel.CanContinue);
    }

    [Fact]
    public async Task DurableBuildStateOverridesSavedWizardStep()
    {
        var resume = new ResumableImageBuild(
            ImageId.Parse("resume-image"),
            "/data/images/.staging-resume",
            WindowsImageBuildPhase.ReadyToVerify,
            DateTimeOffset.UtcNow);
        var firstRun = CreateFirstRun() with
        {
            Startup = CreateFirstRun().Startup with { ResumableBuilds = [resume] },
        };
        firstRun = firstRun with { Readiness = SetupReadinessEvaluator.Evaluate(firstRun.Startup) };
        var state = SetupState.Default with { LastStep = SetupStep.WindowsMedia };
        await using var viewModel = CreateViewModel(
            new FakeSetupService(),
            new FakeStateStore(state),
            firstRun);

        await viewModel.InitializeAsync();

        Assert.Equal(SetupStep.ImageBuild, viewModel.CurrentStep);
        Assert.Equal("resume-image", viewModel.Build.ImageIdText);
        Assert.Equal("/data/images/.staging-resume", viewModel.Build.StagingDirectory);
    }

    [Fact]
    public async Task MultipleWindowsEditionsRequireAnExplicitSelection()
    {
        var runtime = new FakeSetupService { MultipleWindowsEditions = true };
        await using var viewModel = CreateViewModel(
            runtime,
            new FakeStateStore(SetupState.Default with { LastStep = SetupStep.WindowsMedia }),
            CreateFirstRun());
        await viewModel.InitializeAsync();

        await viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        viewModel.IsLicenseConfirmed = true;

        Assert.True(viewModel.HasMultipleWindowsEditions);
        Assert.Null(viewModel.SelectedWindowsEdition);
        Assert.False(viewModel.CanContinue);
        viewModel.SelectedWindowsEdition = viewModel.WindowsEditions.Single(image => image.Index == 6);
        Assert.True(viewModel.CanContinue);
        Assert.Equal(6, viewModel.Build.Installation?.ImageIndex);
    }

    [Fact]
    public async Task PackageKitAbsenceExposesOnlyACopyableManualCommand()
    {
        var state = new FakeStateStore(SetupState.Default with { LastStep = SetupStep.Components });
        await using var viewModel = CreateViewModel(
            new FakeSetupService(),
            state,
            CreateFirstRun());

        await viewModel.InitializeAsync();

        Assert.True(viewModel.ShowManualCommand);
        Assert.StartsWith("sudo apt install -- ", viewModel.ManualInstallCommand, StringComparison.Ordinal);
        Assert.False(viewModel.CanInstallPackages);
    }

    [Fact]
    public async Task DisposalCancelsAndWaitsForActiveMediaValidation()
    {
        var runtime = new FakeSetupService { WaitForMediaCancellation = true };
        var viewModel = CreateViewModel(runtime, new FakeStateStore(SetupState.Default), CreateFirstRun());
        await viewModel.InitializeAsync();
        var validation = viewModel.ValidateWindowsMediaAsync("/media/windows.iso");
        await runtime.MediaValidationStarted.Task;

        await viewModel.DisposeAsync();
        await validation;

        Assert.True(runtime.MediaCancellationObserved);
        Assert.False(viewModel.HasActiveOperation);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SetupWizardViewModel CreateViewModel(
        IDesktopSetupService runtime,
        ISetupStateStore stateStore,
        FirstRunSnapshot firstRun)
    {
        Directory.CreateDirectory(_root);
        var osRelease = Path.Combine(_root, "os-release");
        File.WriteAllText(osRelease, "ID=debian\nNAME=Debian\nVERSION_ID=12\n");
        return new SetupWizardViewModel(
            runtime,
            firstRun,
            stateStore,
            new DistributionInfoReader(osRelease),
            new PackagePlanResolver(new FakeManifestSource()),
            new ManualOnlyPackageInstaller(),
            HostCapacityInspector.Evaluate(
                16L * 1024 * 1024 * 1024,
                128L * 1024 * 1024 * 1024,
                8));
    }

    private static FirstRunSnapshot CreateFirstRun()
    {
        string[] codes =
        [
            QemuDoctorCheckCodes.Platform,
            QemuDoctorCheckCodes.Kvm,
            QemuDoctorCheckCodes.QemuSystem,
            QemuDoctorCheckCodes.QemuImg,
            QemuDoctorCheckCodes.Swtpm,
            QemuDoctorCheckCodes.RemoteViewer,
            QemuDoctorCheckCodes.Passt,
            QemuDoctorCheckCodes.Bubblewrap,
            QemuDoctorCheckCodes.WimlibImagex,
            QemuDoctorCheckCodes.Xorriso,
            QemuDoctorCheckCodes.Firmware,
            QemuDoctorCheckCodes.RuntimeDirectory,
        ];
        var doctor = new QemuDoctorResult(
            new DoctorReport(codes.Select(code => new DoctorCheck(code, true, true, "ready")).ToArray()),
            null,
            null,
            null);
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

    private sealed class FakeSetupService : IDesktopSetupService
    {
        public bool WaitForMediaCancellation { get; init; }

        public bool MultipleWindowsEditions { get; init; }

        public bool MediaCancellationObserved { get; private set; }

        public TaskCompletionSource MediaValidationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int WindowsValidationCount { get; private set; }

        public int VirtioValidationCount { get; private set; }

        public Task<QemuDoctorResult> InspectHostAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateFirstRun().Startup.Doctor);

        public Task<DesktopImageBuildDefaults> GetImageBuildDefaultsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateFirstRun().Startup.ImageBuildDefaults);

        public Task<ImageBuildFileFingerprint> ValidateWindowsMediaAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            WindowsValidationCount++;
            if (!WaitForMediaCancellation)
            {
                return Task.FromResult(Fingerprint(path));
            }

            MediaValidationStarted.TrySetResult();
            return WaitForCancellationAsync(cancellationToken);
        }

        public Task<DesktopWindowsMediaAnalysis> AnalyzeWindowsMediaAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            WindowsValidationCount++;
            if (!WaitForMediaCancellation)
            {
                return Task.FromResult(CreateAnalysis(path));
            }

            MediaValidationStarted.TrySetResult();
            return WaitForAnalysisCancellationAsync(cancellationToken);
        }

        public Task<ImageBuildFileFingerprint> ValidateVirtioMediaAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            VirtioValidationCount++;
            return Task.FromResult(Fingerprint(path));
        }

        public Task<ManagedWindowsImage> BuildImageAsync(
            DesktopImageBuildRequest request,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagedWindowsImage> ResumeImageBuildAsync(
            ImageId imageId,
            string stagingDirectory,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static ImageBuildFileFingerprint Fingerprint(string path) =>
            new(Path.GetFullPath(path), new string('a', 64), 1024, 1);

        private DesktopWindowsMediaAnalysis CreateAnalysis(string path)
        {
            var images = MultipleWindowsEditions
                ? new[]
                {
                    new WindowsInstallationImage(1, "Windows 11 Home", "Core", "amd64", 26100),
                    new WindowsInstallationImage(6, "Windows 11 Pro", "Professional", "amd64", 26100),
                }
                : [new WindowsInstallationImage(6, "Windows 11 Pro", "Professional", "amd64", 26100)];
            return new DesktopWindowsMediaAnalysis(
                Fingerprint(path),
                new WindowsInstallationCatalog(images, MultipleWindowsEditions ? null : 6));
        }

        private async Task<ImageBuildFileFingerprint> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("미디어 검증이 취소되지 않았습니다.");
            }
            catch (OperationCanceledException)
            {
                MediaCancellationObserved = true;
                throw;
            }
        }

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
    }
}
