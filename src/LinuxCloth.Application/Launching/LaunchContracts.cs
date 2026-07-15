using LinuxCloth.Application.Images;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;
using LinuxCloth.Wsb;

namespace LinuxCloth.Application.Launching;

public sealed record LaunchCatalogResolution
{
    public LaunchCatalogResolution(
        IReadOnlyList<ServiceId> serviceIds,
        string catalogPath,
        string catalogSha256,
        IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogSha256);
        ArgumentNullException.ThrowIfNull(displayNames);

        if (serviceIds.Count == 0 || serviceIds.Count > 32 ||
            serviceIds.Any(serviceId => !ServiceId.TryCreate(serviceId.Value, out _)) ||
            serviceIds.Distinct().Count() != serviceIds.Count)
        {
            throw new ArgumentException("The resolved catalog service identifiers are invalid.", nameof(serviceIds));
        }

        if (displayNames.Count != serviceIds.Count ||
            displayNames.Any(displayName => string.IsNullOrWhiteSpace(displayName) ||
                                            displayName.Length > 256 ||
                                            displayName.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "Every resolved service must have a printable display name of at most 256 characters.",
                nameof(displayNames));
        }

        if (!Path.IsPathFullyQualified(catalogPath))
        {
            throw new ArgumentException("The catalog snapshot path must be absolute.", nameof(catalogPath));
        }

        if (catalogSha256.Length != 64 || catalogSha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException("The catalog digest must be a SHA-256 value.", nameof(catalogSha256));
        }

        ServiceIds = Array.AsReadOnly(serviceIds.ToArray());
        CatalogPath = Path.GetFullPath(catalogPath);
        CatalogSha256 = catalogSha256.ToLowerInvariant();
        DisplayNames = Array.AsReadOnly(displayNames.ToArray());
    }

    public IReadOnlyList<ServiceId> ServiceIds { get; }

    public string CatalogPath { get; }

    public string CatalogSha256 { get; }

    public IReadOnlyList<string> DisplayNames { get; }
}

public sealed record VerifiedLaunchImage(
    ImageId ImageId,
    SessionImageDefinition Definition,
    string BaseImageSha256);

public interface ILaunchCatalogResolver
{
    Task<LaunchCatalogResolution> ResolveAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken = default);
}

public interface ILaunchPrerequisiteSource
{
    Task<QemuLaunchPrerequisites> ResolveAsync(
        bool networkEnabled,
        CancellationToken cancellationToken = default);
}

public interface ILaunchImageSource
{
    Task<VerifiedLaunchImage> ResolveAsync(
        ImageId imageId,
        CancellationToken cancellationToken = default);
}

public interface ISessionArtifactService
{
    Task PrepareAsync(
        SessionPaths paths,
        SessionImageDefinition image,
        string qemuImgPath,
        CancellationToken cancellationToken = default);
}

public interface IGuestConfigurationService
{
    Task StageAsync(
        string destinationDirectory,
        GuestLaunchManifest manifest,
        string expressWsb,
        string catalogSnapshotPath,
        CancellationToken cancellationToken = default);
}

public interface IVmSessionStarter
{
    Task<IRunningLinuxClothSession> StartAsync(
        QemuSessionStartRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRunningLinuxClothSession : IAsyncDisposable
{
    Guid SessionId { get; }

    Task Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class LaunchPrerequisiteException : InvalidOperationException
{
    public LaunchPrerequisiteException(DoctorReport report)
        : base("The Linux host does not satisfy every required disposable-VM launch prerequisite.")
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public DoctorReport Report { get; }
}

public sealed class ImageVerificationException : InvalidOperationException
{
    public ImageVerificationException(ImageVerificationResult result)
        : base("The selected Windows base image failed integrity verification.")
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public ImageVerificationResult Result { get; }
}
