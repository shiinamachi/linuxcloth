namespace LinuxCloth.Application.Setup;

public enum SetupOperationDisposition
{
    Completed,
    Blocked,
}

public sealed record SetupOperationCheck(bool IsSatisfied, SetupBlocker? Blocker = null)
{
    public static SetupOperationCheck Required { get; } = new(false);

    public static SetupOperationCheck Satisfied { get; } = new(true);
}

public sealed record SetupOperationResult(
    SetupOperationDisposition Disposition,
    SetupInputSnapshot? UpdatedInputs = null,
    string? ImageBuildStagingDirectory = null,
    SetupBlocker? Blocker = null)
{
    public static SetupOperationResult Completed(
        SetupInputSnapshot? updatedInputs = null,
        string? imageBuildStagingDirectory = null) =>
        new(
            SetupOperationDisposition.Completed,
            updatedInputs,
            imageBuildStagingDirectory);

    public static SetupOperationResult Blocked(SetupBlocker blocker) =>
        new(SetupOperationDisposition.Blocked, Blocker: blocker);
}

public sealed record SetupExecutionContext(SetupRun Run);

public interface ISetupOperation
{
    SetupPhase Phase { get; }

    string Status { get; }

    Task<SetupOperationCheck> CheckAsync(
        SetupExecutionContext context,
        CancellationToken cancellationToken);

    Task<SetupOperationResult> ExecuteAsync(
        SetupExecutionContext context,
        IProgress<SetupProgress> progress,
        CancellationToken cancellationToken);
}

public sealed class SetupOperationBlockedException : Exception
{
    public SetupOperationBlockedException(SetupBlocker blocker)
        : base((blocker ?? throw new ArgumentNullException(nameof(blocker))).Description)
    {
        Blocker = blocker;
    }

    public SetupBlocker Blocker { get; }
}
