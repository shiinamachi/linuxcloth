using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Services;

public sealed record DesktopWindowsMediaAnalysis(
    ImageBuildFileFingerprint Fingerprint,
    WindowsInstallationCatalog Catalog);

public interface IDesktopMediaValidationService
{
    Task<ImageBuildFileFingerprint> ValidateWindowsMediaAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<DesktopWindowsMediaAnalysis> AnalyzeWindowsMediaAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<ImageBuildFileFingerprint> ValidateVirtioMediaAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IDesktopSetupService : IDesktopImageBuildService, IDesktopMediaValidationService
{
    Task<QemuDoctorResult> InspectHostAsync(CancellationToken cancellationToken = default);
}
