using LinuxCloth.Application.Images;

namespace LinuxCloth.Application.Setup;

public enum SetupPhase
{
    Discovering,
    AwaitingInputs,
    InstallingDependencies,
    ValidatingMedia,
    PlanningWindows,
    BuildingImage,
    Finalizing,
    Blocked,
    Completed,
}

public enum SetupFailureKind
{
    UserActionRequired,
    Retryable,
    Fatal,
    UnsafeToResume,
}

public sealed record SetupFileFingerprint(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record SetupInputSnapshot(
    string? WindowsIsoPath,
    SetupFileFingerprint? WindowsIsoFingerprint,
    string? VirtioIsoPath,
    SetupFileFingerprint? VirtioIsoFingerprint,
    int? WindowsImageIndex,
    string? WindowsEditionId,
    string? WindowsEdition,
    string? PackagePlanDigest,
    ImageId ImageId,
    int DiskSizeGiB,
    int CpuCount,
    int MemoryMiB,
    bool LicenseConfirmed)
{
    public SetupInputSnapshot RedactMediaPaths() => this with
    {
        WindowsIsoPath = null,
        VirtioIsoPath = null,
    };
}

public sealed record SetupBlocker(
    SetupPhase Phase,
    SetupFailureKind Kind,
    string Code,
    string Title,
    string Description,
    string ActionLabel,
    string? TechnicalDetail = null);

public sealed record SetupRun(
    int SchemaVersion,
    Guid RunId,
    SetupPhase Phase,
    SetupInputSnapshot Inputs,
    string? ImageBuildStagingDirectory,
    SetupBlocker? Blocker,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsActive => Phase is not SetupPhase.Completed;

    public static SetupRun Create(SetupInputSnapshot inputs, DateTimeOffset now) => new(
        CurrentSchemaVersion,
        Guid.NewGuid(),
        SetupPhase.Discovering,
        inputs,
        ImageBuildStagingDirectory: null,
        Blocker: null,
        Attempt: 0,
        StartedAt: now,
        UpdatedAt: now);
}

public sealed record SetupProgress(
    SetupPhase Phase,
    string Status,
    int CompletedOperations,
    int TotalOperations);
