using System.Security.Cryptography;
using System.Text;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Setup;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Setup;

public static class DesktopSetupOperationFactory
{
    private static readonly string[] ImageBuildChecks =
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
        QemuDoctorCheckCodes.WimlibImagex,
        QemuDoctorCheckCodes.SevenZip,
        QemuDoctorCheckCodes.Xorriso,
    ];

    public static ISetupOperation[] Create(
        IDesktopSetupService runtime,
        DistributionInfoReader distributionReader,
        PackagePlanResolver packagePlanResolver,
        IPackageInstaller packageInstaller,
        IProgress<DesktopImageBuildProgress>? imageBuildProgress = null) =>
    [
        new HostInspectionOperation(runtime),
        new InputOperation(),
        new DependencyOperation(
            runtime,
            distributionReader,
            packagePlanResolver,
            packageInstaller),
        new MediaOperation(runtime),
        new WindowsPlanningOperation(),
        new ImageBuildOperation(runtime, imageBuildProgress),
        new FinalizingOperation(runtime),
    ];

    public static bool CanBuildImage(QemuDoctorResult doctor) =>
        ImageBuildChecks.All(code => IsAvailable(doctor, code));

    private static bool IsAvailable(QemuDoctorResult doctor, string code) =>
        doctor.Report.Checks.Any(check =>
            string.Equals(check.Name, code, StringComparison.Ordinal) && check.IsAvailable);

    private sealed class HostInspectionOperation : ISetupOperation
    {
        private readonly IDesktopSetupService _runtime;

        public HostInspectionOperation(IDesktopSetupService runtime)
        {
            _runtime = runtime;
        }

        public SetupPhase Phase => SetupPhase.Discovering;

        public string Status => "이 컴퓨터를 확인하고 있습니다.";

        public Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SetupOperationCheck.Required);
        }

        public async Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = context;
            _ = progress;
            var doctor = await _runtime.InspectHostAsync(cancellationToken).ConfigureAwait(false);
            if (!IsAvailable(doctor, QemuDoctorCheckCodes.Platform))
            {
                return SetupOperationResult.Blocked(
                    Blocker(
                        SetupFailureKind.Fatal,
                        "SETUP-HOST-PLATFORM",
                        "지원되는 Linux 환경이 필요합니다",
                        "이 컴퓨터에서는 Windows 환경을 준비할 수 없습니다.",
                        "다시 확인"));
            }

            if (!IsAvailable(doctor, QemuDoctorCheckCodes.Kvm))
            {
                return SetupOperationResult.Blocked(
                    Blocker(
                        SetupFailureKind.Fatal,
                        "SETUP-HOST-KVM",
                        "가상화 기능을 사용할 수 없습니다",
                        "펌웨어 가상화 설정과 현재 사용자 권한을 확인하세요.",
                        "다시 확인"));
            }

            var defaults = await _runtime.GetImageBuildDefaultsAsync(cancellationToken)
                .ConfigureAwait(false);
            return defaults.IsGuestBridgeAvailable
                ? SetupOperationResult.Completed()
                : SetupOperationResult.Blocked(
                    Blocker(
                        SetupFailureKind.Fatal,
                        "SETUP-HOST-GUEST-COMPONENT",
                        "Windows 연결 구성 요소가 없습니다",
                        "linuxcloth 설치 파일이 완전한지 확인한 뒤 앱을 다시 설치하세요.",
                        "다시 확인"));
        }

        private SetupBlocker Blocker(
            SetupFailureKind kind,
            string code,
            string title,
            string description,
            string action) =>
            new(Phase, kind, code, title, description, action);
    }

    private sealed class InputOperation : ISetupOperation
    {
        public SetupPhase Phase => SetupPhase.AwaitingInputs;

        public string Status => "선택한 설치 파일을 확인하고 있습니다.";

        public Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = context.Run.Inputs;
            SetupBlocker? blocker = !inputs.LicenseConfirmed
                ? CreateBlocker("SETUP-INPUT-LICENSE", "Windows 사용 권한을 확인하세요.")
                : !IsAbsolutePath(inputs.WindowsIsoPath)
                    ? CreateBlocker("SETUP-INPUT-WINDOWS", "Windows 11 설치 파일을 다시 선택하세요.")
                    : inputs.VirtioIsoPath is not null && !IsAbsolutePath(inputs.VirtioIsoPath)
                        ? CreateBlocker("SETUP-INPUT-DRIVERS", "Windows 장치 드라이버 파일을 다시 선택하세요.")
                        : !HasInstallationSelection(inputs)
                            ? CreateBlocker("SETUP-WINDOWS-IMAGE-AMBIGUOUS", "설치할 Windows 버전을 선택하세요.")
                            : null;
            return Task.FromResult(
                blocker is null
                    ? SetupOperationCheck.Satisfied
                    : new SetupOperationCheck(false, blocker));
        }

        public Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = context;
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SetupOperationResult.Completed());
        }

        private SetupBlocker CreateBlocker(string code, string description) =>
            new(
                Phase,
                SetupFailureKind.UserActionRequired,
                code,
                "설치 준비 항목을 확인하세요",
                description,
                "입력 확인");
    }

    private sealed class DependencyOperation : ISetupOperation
    {
        private readonly DistributionInfoReader _distributionReader;
        private readonly IPackageInstaller _packageInstaller;
        private readonly PackagePlanResolver _packagePlanResolver;
        private readonly IDesktopSetupService _runtime;

        public DependencyOperation(
            IDesktopSetupService runtime,
            DistributionInfoReader distributionReader,
            PackagePlanResolver packagePlanResolver,
            IPackageInstaller packageInstaller)
        {
            _runtime = runtime;
            _distributionReader = distributionReader;
            _packagePlanResolver = packagePlanResolver;
            _packageInstaller = packageInstaller;
        }

        public SetupPhase Phase => SetupPhase.InstallingDependencies;

        public string Status => "필수 구성 요소를 준비하고 있습니다.";

        public async Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            var doctor = await _runtime.InspectHostAsync(cancellationToken).ConfigureAwait(false);
            return CanBuildImage(doctor)
                ? SetupOperationCheck.Satisfied
                : SetupOperationCheck.Required;
        }

        public async Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            var distribution = await _distributionReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (distribution.Family == DistributionFamily.Unsupported)
            {
                return SetupOperationResult.Blocked(
                    CreateBlocker(
                        SetupFailureKind.Fatal,
                        "SETUP-PKG-DISTRIBUTION",
                        "이 Linux 배포판에서는 자동 준비를 지원하지 않습니다",
                        "배포판 문서에 따라 필요한 구성 요소를 설치한 뒤 다시 확인하세요.",
                        "다시 확인"));
            }

            var plan = await _packagePlanResolver.ResolveAsync(distribution, cancellationToken)
                .ConfigureAwait(false);
            var preview = await _packageInstaller.ResolveAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            if (!preview.IsPackageKitAvailable)
            {
                return SetupOperationResult.Blocked(
                    CreateBlocker(
                        SetupFailureKind.UserActionRequired,
                        "SETUP-PKG-MANUAL",
                        "필수 구성 요소를 직접 설치해야 합니다",
                        "아래 명령을 터미널에서 실행한 뒤 다시 확인하세요.",
                        "다시 확인",
                        plan.ManualInstallCommand));
            }

            if (preview.UnresolvedPackages.Count > 0)
            {
                return SetupOperationResult.Blocked(
                    CreateBlocker(
                        SetupFailureKind.Retryable,
                        "SETUP-PKG-UNRESOLVED",
                        "일부 구성 요소를 찾지 못했습니다",
                        "패키지 저장소를 새로 고친 뒤 다시 시도하세요.",
                        "다시 시도",
                        string.Join(", ", preview.UnresolvedPackages)));
            }

            if (preview.CanInstall)
            {
                var packageProgress = new ImmediateProgress<PackageInstallProgress>(item =>
                    progress.Report(
                        new SetupProgress(
                            Phase,
                            string.IsNullOrWhiteSpace(item.PackageName)
                                ? item.Status
                                : $"{item.Status} · {item.PackageName}",
                            0,
                            1)));
                var result = await _packageInstaller.InstallAsync(
                        preview,
                        packageProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    return SetupOperationResult.Blocked(
                        CreateBlocker(
                            SetupFailureKind.Retryable,
                            "SETUP-PKG-INSTALL",
                            "필수 구성 요소 설치를 마치지 못했습니다",
                            "시스템 인증을 확인한 뒤 다시 시도하세요.",
                            "다시 시도",
                            result.Message));
                }
            }

            var refreshed = await _runtime.InspectHostAsync(cancellationToken).ConfigureAwait(false);
            if (!CanBuildImage(refreshed))
            {
                return SetupOperationResult.Blocked(
                    CreateBlocker(
                        SetupFailureKind.Retryable,
                        "SETUP-PKG-READINESS",
                        "필수 구성 요소가 아직 준비되지 않았습니다",
                        "설치 상태를 다시 확인하세요.",
                        "다시 확인"));
            }

            return SetupOperationResult.Completed(
                context.Run.Inputs with { PackagePlanDigest = CreatePlanDigest(plan) });
        }

        private SetupBlocker CreateBlocker(
            SetupFailureKind kind,
            string code,
            string title,
            string description,
            string action,
            string? technicalDetail = null) =>
            new(Phase, kind, code, title, description, action, technicalDetail);

        private static string CreatePlanDigest(PackagePlan plan)
        {
            var canonical = $"{plan.Family}\n{string.Join('\n', plan.AllPackages)}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }
    }

    private sealed class MediaOperation : ISetupOperation
    {
        private readonly IDesktopSetupService _runtime;

        public MediaOperation(IDesktopSetupService runtime)
        {
            _runtime = runtime;
        }

        public SetupPhase Phase => SetupPhase.ValidatingMedia;

        public string Status => "설치 파일이 변경되지 않았는지 확인하고 있습니다.";

        public Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SetupOperationCheck.Required);
        }

        public async Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            var inputs = context.Run.Inputs;
            var windows = await _runtime.AnalyzeWindowsMediaAsync(
                    inputs.WindowsIsoPath!,
                    cancellationToken)
                .ConfigureAwait(false);
            var selected = windows.Catalog.SupportedImages.SingleOrDefault(
                image => image.Index == inputs.WindowsImageIndex);
            if (selected is null ||
                !string.Equals(selected.EditionId, inputs.WindowsEditionId, StringComparison.Ordinal))
            {
                return SetupOperationResult.Blocked(
                    new SetupBlocker(
                        Phase,
                        SetupFailureKind.UserActionRequired,
                        "SETUP-WINDOWS-IMAGE-CHANGED",
                        "Windows 설치 파일의 버전 구성이 바뀌었습니다",
                        "설치 파일을 다시 선택하고 Windows 버전을 확인하세요.",
                        "파일 다시 선택"));
            }

            var virtioPath = inputs.VirtioIsoPath;
            if (string.IsNullOrWhiteSpace(virtioPath))
            {
                var downloadProgress = new ImmediateProgress<VirtioMediaDownloadProgress>(item =>
                    progress.Report(
                        new SetupProgress(
                            Phase,
                            item.Status,
                            0,
                            1)));
                try
                {
                    var pinned = await _runtime.PreparePinnedVirtioMediaAsync(
                            downloadProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    virtioPath = pinned.Path;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or HttpRequestException or InvalidDataException)
                {
                    return SetupOperationResult.Blocked(
                        new SetupBlocker(
                            Phase,
                            SetupFailureKind.Retryable,
                            "SETUP-MEDIA-DRIVER-DOWNLOAD",
                            "Windows 장치 드라이버를 준비하지 못했습니다",
                            "네트워크 연결을 확인해 다시 시도하거나 로컬 ISO 파일을 선택하세요.",
                            "다시 시도",
                            exception.Message));
                }
            }

            var virtio = await _runtime.ValidateVirtioMediaAsync(virtioPath, cancellationToken)
                .ConfigureAwait(false);
            return SetupOperationResult.Completed(
                inputs with
                {
                    WindowsIsoFingerprint = ToSetupFingerprint(windows.Fingerprint),
                    VirtioIsoPath = virtio.Path,
                    VirtioIsoFingerprint = ToSetupFingerprint(virtio),
                    WindowsEdition = selected.DisplayName,
                });
        }
    }

    private sealed class WindowsPlanningOperation : ISetupOperation
    {
        public SetupPhase Phase => SetupPhase.PlanningWindows;

        public string Status => "Windows 자동 설치 계획을 준비하고 있습니다.";

        public Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputs = context.Run.Inputs;
            return Task.FromResult(
                HasInstallationSelection(inputs)
                    ? SetupOperationCheck.Satisfied
                    : new SetupOperationCheck(
                        false,
                        new SetupBlocker(
                            Phase,
                            SetupFailureKind.UserActionRequired,
                            "SETUP-WINDOWS-IMAGE-AMBIGUOUS",
                            "설치할 Windows 버전이 필요합니다",
                            "설치 파일을 다시 확인하고 Windows 버전을 선택하세요.",
                            "버전 선택")));
        }

        public Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = context;
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(SetupOperationResult.Completed());
        }
    }

    private sealed class ImageBuildOperation : ISetupOperation
    {
        private readonly IProgress<DesktopImageBuildProgress>? _imageBuildProgress;
        private readonly IDesktopSetupService _runtime;

        public ImageBuildOperation(
            IDesktopSetupService runtime,
            IProgress<DesktopImageBuildProgress>? imageBuildProgress)
        {
            _runtime = runtime;
            _imageBuildProgress = imageBuildProgress;
        }

        public SetupPhase Phase => SetupPhase.BuildingImage;

        public string Status => "Windows를 자동으로 설치하고 있습니다.";

        public async Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken) =>
            await _runtime.HasVerifiedImageAsync(context.Run.Inputs.ImageId, cancellationToken)
                .ConfigureAwait(false)
                ? SetupOperationCheck.Satisfied
                : SetupOperationCheck.Required;

        public async Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            var inputs = context.Run.Inputs;
            var staging = context.Run.ImageBuildStagingDirectory;
            if (string.IsNullOrWhiteSpace(staging))
            {
                var resumable = await _runtime.FindResumableBuildsAsync(cancellationToken)
                    .ConfigureAwait(false);
                staging = resumable.Builds
                    .Where(build => build.ImageId == inputs.ImageId)
                    .OrderByDescending(static build => build.UpdatedAt)
                    .Select(static build => build.StagingDirectory)
                    .FirstOrDefault();
            }

            string? reportedStaging = staging;
            var buildProgress = new ImmediateProgress<DesktopImageBuildProgress>(item =>
            {
                reportedStaging = item.StagingDirectory ?? reportedStaging;
                _imageBuildProgress?.Report(item);
                progress.Report(
                    new SetupProgress(
                        Phase,
                        BuildStatus(item.Phase),
                        0,
                        1));
            });

            try
            {
                if (!string.IsNullOrWhiteSpace(staging))
                {
                    _ = await _runtime.ResumeImageBuildAsync(
                            inputs.ImageId,
                            staging,
                            buildProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var defaults = await _runtime.GetImageBuildDefaultsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!defaults.IsGuestBridgeAvailable ||
                        defaults.OvmfCodePath is null ||
                        defaults.OvmfVariablesTemplatePath is null)
                    {
                        return SetupOperationResult.Blocked(
                            CreateBuildBlocker(
                                SetupFailureKind.Fatal,
                                "SETUP-BUILD-DEFAULTS",
                                "Windows 환경 구성 요소가 준비되지 않았습니다",
                                "필수 구성 요소를 다시 확인하세요.",
                                "다시 확인"));
                    }

                    _ = await _runtime.BuildImageAsync(
                            new DesktopImageBuildRequest(
                                inputs.ImageId,
                                inputs.WindowsIsoPath!,
                                inputs.VirtioIsoPath!,
                                defaults.GuestBridgeExecutablePath,
                                defaults.OvmfCodePath,
                                defaults.OvmfVariablesTemplatePath,
                                inputs.DiskSizeGiB,
                                inputs.CpuCount,
                                inputs.MemoryMiB,
                                new WindowsInstallationSelection(
                                    inputs.WindowsImageIndex!.Value,
                                    inputs.WindowsEditionId!,
                                    inputs.WindowsEdition!)),
                            buildProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return SetupOperationResult.Completed(imageBuildStagingDirectory: reportedStaging);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WindowsImageBuildException exception)
            {
                return SetupOperationResult.Blocked(
                    CreateBuildBlocker(
                        SetupFailureKind.Retryable,
                        "SETUP-BUILD-WINDOWS",
                        "Windows 환경 준비를 마치지 못했습니다",
                        "보존된 작업에서 다시 시도할 수 있습니다.",
                        "다시 시도",
                        exception.Message));
            }
        }

        private SetupBlocker CreateBuildBlocker(
            SetupFailureKind kind,
            string code,
            string title,
            string description,
            string action,
            string? technicalDetail = null) =>
            new(Phase, kind, code, title, description, action, technicalDetail);

        private static string BuildStatus(WindowsImageBuildPhase phase) => phase switch
        {
            WindowsImageBuildPhase.Preparing or WindowsImageBuildPhase.Prepared =>
                "Windows 설치 환경을 만들고 있습니다.",
            WindowsImageBuildPhase.InstallerRunning => "Windows를 자동으로 설치하고 있습니다.",
            WindowsImageBuildPhase.ReadyToVerify or WindowsImageBuildPhase.VerificationRunning =>
                "설치된 Windows 환경을 확인하고 있습니다.",
            WindowsImageBuildPhase.ReadyToFinalize => "Windows 환경을 마무리하고 있습니다.",
            _ => "Windows 환경을 준비하고 있습니다.",
        };
    }

    private sealed class FinalizingOperation : ISetupOperation
    {
        private readonly IDesktopSetupService _runtime;

        public FinalizingOperation(IDesktopSetupService runtime)
        {
            _runtime = runtime;
        }

        public SetupPhase Phase => SetupPhase.Finalizing;

        public string Status => "Windows 환경을 마무리하고 있습니다.";

        public async Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken) =>
            await _runtime.HasVerifiedImageAsync(context.Run.Inputs.ImageId, cancellationToken)
                .ConfigureAwait(false)
                ? SetupOperationCheck.Satisfied
                : SetupOperationCheck.Required;

        public async Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = progress;
            return await _runtime.HasVerifiedImageAsync(context.Run.Inputs.ImageId, cancellationToken)
                .ConfigureAwait(false)
                ? SetupOperationResult.Completed()
                : SetupOperationResult.Blocked(
                    new SetupBlocker(
                        Phase,
                        SetupFailureKind.Fatal,
                        "SETUP-FINALIZE-VERIFY",
                        "Windows 환경 확인을 완료하지 못했습니다",
                        "진단 세부정보를 확인한 뒤 작업을 다시 시도하세요.",
                        "다시 시도"));
        }
    }

    private static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool HasInstallationSelection(SetupInputSnapshot inputs) =>
        inputs.WindowsImageIndex > 0 &&
        !string.IsNullOrWhiteSpace(inputs.WindowsEditionId) &&
        !string.IsNullOrWhiteSpace(inputs.WindowsEdition);

    private static SetupFileFingerprint ToSetupFingerprint(ImageBuildFileFingerprint fingerprint) =>
        new(
            fingerprint.Length,
            new DateTimeOffset(fingerprint.LastWriteUtcTicks, TimeSpan.Zero),
            fingerprint.Sha256);

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Action<T> _report = report ?? throw new ArgumentNullException(nameof(report));

        public void Report(T value) => _report(value);
    }
}
