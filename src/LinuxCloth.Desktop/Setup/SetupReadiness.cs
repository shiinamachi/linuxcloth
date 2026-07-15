using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Setup;

public enum SetupRoute
{
    RecoveryRequired,
    FirstRun,
    EnvironmentRepair,
    Main,
}

public sealed record ResumableImageBuild(
    ImageId ImageId,
    string StagingDirectory,
    WindowsImageBuildPhase Phase,
    DateTimeOffset UpdatedAt);

public sealed record ImageBuildRecoveryIssue(string StagingDirectory, string Message);

public sealed record SetupReadiness(
    bool HasUnresolvedRecovery,
    bool HasVerifiedImage,
    bool CanBuildImage,
    bool CanLaunchOffline,
    bool CanLaunchOnline,
    bool HasResumableBuild,
    bool IsGuestBridgeAvailable,
    bool HasCompatibleFirmware,
    SetupRoute Route,
    ResumableImageBuild? PreferredResumableBuild);

public static class SetupReadinessEvaluator
{
    private static readonly string[] OfflineLaunchChecks =
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
    ];

    private static readonly string[] ImageBuildChecks =
    [
        .. OfflineLaunchChecks,
        QemuDoctorCheckCodes.WimlibImagex,
        QemuDoctorCheckCodes.Xorriso,
    ];

    public static SetupReadiness Evaluate(DesktopStartupSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var hasUnresolvedRecovery = snapshot.Recovery.Any(result => !result.IsCleaned);
        var hasVerifiedImage = snapshot.ImageVerification.Any(result => result.IsValid);
        var hasCompatibleFirmware =
            snapshot.ImageBuildDefaults.OvmfCodePath is not null &&
            snapshot.ImageBuildDefaults.OvmfVariablesTemplatePath is not null &&
            IsAvailable(snapshot.Doctor, QemuDoctorCheckCodes.Firmware);
        var canBuildImage =
            snapshot.ImageBuildDefaults.IsGuestBridgeAvailable &&
            hasCompatibleFirmware &&
            AreAvailable(snapshot.Doctor, ImageBuildChecks);
        var canLaunchOffline = hasVerifiedImage && AreAvailable(snapshot.Doctor, OfflineLaunchChecks);
        var canLaunchOnline =
            canLaunchOffline &&
            IsAvailable(snapshot.Doctor, QemuDoctorCheckCodes.Passt);
        var preferredResumableBuild = snapshot.ResumableBuilds
            .OrderByDescending(build => build.UpdatedAt)
            .FirstOrDefault();
        var route = hasUnresolvedRecovery
            ? SetupRoute.RecoveryRequired
            : !hasVerifiedImage
                ? SetupRoute.FirstRun
                : !canLaunchOnline
                    ? SetupRoute.EnvironmentRepair
                    : SetupRoute.Main;

        return new SetupReadiness(
            hasUnresolvedRecovery,
            hasVerifiedImage,
            canBuildImage,
            canLaunchOffline,
            canLaunchOnline,
            preferredResumableBuild is not null,
            snapshot.ImageBuildDefaults.IsGuestBridgeAvailable,
            hasCompatibleFirmware,
            route,
            preferredResumableBuild);
    }

    private static bool AreAvailable(QemuDoctorResult doctor, IEnumerable<string> codes) =>
        codes.All(code => IsAvailable(doctor, code));

    private static bool IsAvailable(QemuDoctorResult doctor, string code) =>
        doctor.Report.Checks.Any(check =>
            string.Equals(check.Name, code, StringComparison.Ordinal) && check.IsAvailable);
}

public sealed record FirstRunSnapshot(
    DesktopStartupSnapshot Startup,
    SetupReadiness Readiness);

public sealed class FirstRunCoordinator
{
    private readonly DesktopRuntime _runtime;

    public FirstRunCoordinator(DesktopRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<FirstRunSnapshot> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var startup = await _runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return new FirstRunSnapshot(startup, SetupReadinessEvaluator.Evaluate(startup));
    }
}
