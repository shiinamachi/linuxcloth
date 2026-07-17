using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Cli;

public abstract record CliCommand;

public sealed record HelpCommand(string? Topic = null) : CliCommand;

public sealed record VersionCommand : CliCommand;

public sealed record DoctorCommand : CliCommand;

public sealed record CatalogListCommand(
    CatalogCategory? Category,
    string? CatalogRoot) : CliCommand;

public sealed record CatalogSearchCommand(
    string Query,
    CatalogCategory? Category,
    string? CatalogRoot) : CliCommand;

public sealed record ImageListCommand : CliCommand;

public sealed record ImageVerifyCommand(ImageId ImageId) : CliCommand;

public sealed record CleanupCommand : CliCommand;

public sealed record RunCommand(
    IReadOnlyList<ServiceId> ServiceIds,
    ImageId ImageId,
    int CpuCount,
    int MemoryMiB,
    bool NetworkEnabled,
    bool ClipboardEnabled,
    string? CatalogRoot) : CliCommand;

public sealed record ImageBuildStartCommand(
    ImageId ImageId,
    string WindowsIsoPath,
    string VirtioWinIsoPath,
    string GuestBridgeExecutablePath,
    int DiskSizeGiB,
    int CpuCount,
    int MemoryMiB,
    int? WindowsImageIndex = null) : CliCommand;

public sealed record ImageBuildResumeCommand(
    ImageId ImageId,
    string StagingDirectory) : CliCommand;

public sealed record ImageBuildRecoverCommand(
    ImageId ImageId,
    string StagingDirectory) : CliCommand;

public sealed record ImageBuildProgress(
    WindowsImageBuildPhase Phase,
    string? StagingDirectory);

public sealed record CliParseResult(CliCommand? Command, string? Error)
{
    public bool IsSuccess => Command is not null && Error is null;

    public static CliParseResult Success(CliCommand command) => new(command, null);

    public static CliParseResult Failure(string error) => new(null, error);
}

public enum CliExitCode
{
    Success = 0,
    Usage = 2,
    HostUnavailable = 3,
    NotFound = 4,
    IntegrityFailure = 5,
    CleanupIncomplete = 6,
    ImageBuildFailure = 7,
    SoftwareError = 70,
    Cancelled = 130,
}

public sealed class CliCommandException : Exception
{
    public CliCommandException(CliExitCode exitCode, string message)
        : base(message)
    {
        if (exitCode is CliExitCode.Success or CliExitCode.Usage or CliExitCode.Cancelled)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exitCode),
                "A command failure must use a non-success runtime exit code.");
        }

        ExitCode = exitCode;
    }

    public CliExitCode ExitCode { get; }
}
