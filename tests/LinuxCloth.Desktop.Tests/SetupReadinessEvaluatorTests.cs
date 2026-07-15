using System.Globalization;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Desktop.Tests;

public sealed class SetupReadinessEvaluatorTests
{
    [Fact]
    public void RoutesNewHostToFirstRunAndSeparatesBuildFromOnlineReadiness()
    {
        var snapshot = CreateSnapshot(
            verifiedImage: false,
            missingCheck: QemuDoctorCheckCodes.Passt);

        var readiness = SetupReadinessEvaluator.Evaluate(snapshot);

        Assert.Equal(SetupRoute.FirstRun, readiness.Route);
        Assert.True(readiness.CanBuildImage);
        Assert.False(readiness.CanLaunchOffline);
        Assert.False(readiness.CanLaunchOnline);
    }

    [Fact]
    public void RoutesVerifiedOfflineOnlyHostToEnvironmentRepair()
    {
        var snapshot = CreateSnapshot(
            verifiedImage: true,
            missingCheck: QemuDoctorCheckCodes.Passt);

        var readiness = SetupReadinessEvaluator.Evaluate(snapshot);

        Assert.Equal(SetupRoute.EnvironmentRepair, readiness.Route);
        Assert.True(readiness.CanLaunchOffline);
        Assert.False(readiness.CanLaunchOnline);
    }

    [Fact]
    public void RecoveryAlwaysTakesPriorityOverAReadyHost()
    {
        var recovery = new RecoveryResult(
            "/run/user/1000/linuxcloth/sessions/broken",
            null,
            RecoveryDisposition.PreservedInvalidRecord);
        var snapshot = CreateSnapshot(verifiedImage: true) with { Recovery = [recovery] };

        var readiness = SetupReadinessEvaluator.Evaluate(snapshot);

        Assert.Equal(SetupRoute.RecoveryRequired, readiness.Route);
        Assert.True(readiness.HasUnresolvedRecovery);
    }

    [Fact]
    public void SelectsTheNewestDurableBuildStateForResume()
    {
        var older = new ResumableImageBuild(
            ImageId.Parse("older"),
            "/images/.staging-older-a",
            WindowsImageBuildPhase.Prepared,
            DateTimeOffset.Parse("2026-07-14T00:00:00Z", CultureInfo.InvariantCulture));
        var newer = new ResumableImageBuild(
            ImageId.Parse("newer"),
            "/images/.staging-newer-b",
            WindowsImageBuildPhase.ReadyToVerify,
            DateTimeOffset.Parse("2026-07-15T00:00:00Z", CultureInfo.InvariantCulture));
        var snapshot = CreateSnapshot(verifiedImage: false) with
        {
            ResumableBuilds = [older, newer],
        };

        var readiness = SetupReadinessEvaluator.Evaluate(snapshot);

        Assert.True(readiness.HasResumableBuild);
        Assert.Equal(newer, readiness.PreferredResumableBuild);
    }

    [Fact]
    public void MissingBundledGuestBridgePreventsImageBuild()
    {
        var snapshot = CreateSnapshot(verifiedImage: false) with
        {
            ImageBuildDefaults = new DesktopImageBuildDefaults(
                "/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe",
                false,
                "/usr/share/OVMF_CODE.fd",
                "/usr/share/OVMF_VARS.fd"),
        };

        var readiness = SetupReadinessEvaluator.Evaluate(snapshot);

        Assert.False(readiness.CanBuildImage);
        Assert.False(readiness.IsGuestBridgeAvailable);
    }

    private static DesktopStartupSnapshot CreateSnapshot(
        bool verifiedImage,
        string? missingCheck = null)
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
        var checks = codes
            .Select(code => new DoctorCheck(
                code,
                IsRequired: true,
                IsAvailable: !string.Equals(code, missingCheck, StringComparison.Ordinal),
                "test"))
            .ToArray();
        var imageId = ImageId.Parse("windows-11");
        IReadOnlyList<ImageVerificationResult> verification = verifiedImage
            ? [new ImageVerificationResult(imageId, [])]
            : [];
        return new DesktopStartupSnapshot(
            null!,
            [],
            verification,
            new QemuDoctorResult(new DoctorReport(checks), null, null, null),
            [],
            new DesktopImageBuildDefaults(
                "/usr/lib/linuxcloth/guest/linuxcloth-guest-bridge.exe",
                true,
                "/usr/share/OVMF_CODE.fd",
                "/usr/share/OVMF_VARS.fd"),
            [],
            []);
    }
}
