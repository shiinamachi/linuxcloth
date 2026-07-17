using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;

namespace LinuxCloth.Desktop.Services;

public sealed record DesktopImageBuildDefaults(
    string GuestBridgeExecutablePath,
    bool IsGuestBridgeAvailable,
    string? OvmfCodePath,
    string? OvmfVariablesTemplatePath);

public sealed record DesktopImageBuildRequest(
    ImageId ImageId,
    string WindowsIsoPath,
    string VirtioWinIsoPath,
    string GuestBridgeExecutablePath,
    string OvmfCodePath,
    string OvmfVariablesTemplatePath,
    int DiskSizeGiB,
    int CpuCount,
    int MemoryMiB,
    WindowsInstallationSelection Installation);

public sealed record DesktopImageBuildProgress(
    WindowsImageBuildPhase Phase,
    string? StagingDirectory,
    bool IsRecovery = false);

public interface IDesktopImageBuildService
{
    Task<DesktopImageBuildDefaults> GetImageBuildDefaultsAsync(
        CancellationToken cancellationToken = default);

    Task<ManagedWindowsImage> BuildImageAsync(
        DesktopImageBuildRequest request,
        IProgress<DesktopImageBuildProgress> progress,
        CancellationToken cancellationToken = default);

    Task<ManagedWindowsImage> ResumeImageBuildAsync(
        ImageId imageId,
        string stagingDirectory,
        IProgress<DesktopImageBuildProgress> progress,
        CancellationToken cancellationToken = default);
}
