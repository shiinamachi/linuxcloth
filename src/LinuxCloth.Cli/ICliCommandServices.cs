using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Cli;

public interface ICliCommandServices
{
    Task<DoctorReport> InspectHostAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogServiceEntry>> QueryCatalogAsync(
        string? query,
        LinuxCloth.Catalog.CatalogCategory? category,
        string? catalogRoot,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedWindowsImage>> ListImagesAsync(CancellationToken cancellationToken);

    Task<ImageVerificationResult> VerifyImageAsync(
        ImageId imageId,
        CancellationToken cancellationToken);

    Task<ManagedWindowsImage> BuildImageAsync(
        ImageBuildStartCommand command,
        IProgress<ImageBuildProgress> progress,
        CancellationToken cancellationToken);

    Task<ManagedWindowsImage> ResumeImageBuildAsync(
        ImageBuildResumeCommand command,
        IProgress<ImageBuildProgress> progress,
        CancellationToken cancellationToken);

    Task<WindowsImageBuildWorkspace> RecoverImageBuildAsync(
        ImageBuildRecoverCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecoveryResult>> CleanupSessionsAsync(CancellationToken cancellationToken);

    Task<Guid> RunSessionAsync(
        RunCommand command,
        IProgress<SessionState> progress,
        CancellationToken cancellationToken);
}
