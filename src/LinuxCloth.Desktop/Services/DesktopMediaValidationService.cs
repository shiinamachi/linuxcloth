using LinuxCloth.Application.ImageBuilding;

namespace LinuxCloth.Desktop.Services;

public interface IDesktopMediaValidationService
{
    Task<ImageBuildFileFingerprint> ValidateWindowsMediaAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<ImageBuildFileFingerprint> ValidateVirtioMediaAsync(
        string path,
        CancellationToken cancellationToken = default);
}
