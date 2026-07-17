namespace LinuxCloth.Application.Setup;

public sealed class SetupOrchestrator : IDisposable
{
    private static readonly SetupPhase[] CanonicalPhaseOrder =
    [
        SetupPhase.Discovering,
        SetupPhase.AwaitingInputs,
        SetupPhase.InstallingDependencies,
        SetupPhase.ValidatingMedia,
        SetupPhase.PlanningWindows,
        SetupPhase.BuildingImage,
        SetupPhase.Finalizing,
    ];

    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly ISetupRunStore _store;
    private readonly ISetupOperation[] _operations;

    public SetupOrchestrator(
        ISetupRunStore store,
        IEnumerable<ISetupOperation> operations,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(operations);
        _operations = ValidateOperations(operations);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SetupRun> StartAsync(
        SetupInputSnapshot inputs,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (existing?.IsActive == true)
            {
                throw new InvalidOperationException("An active setup run must be resumed instead of replaced.");
            }

            var run = SetupRun.Create(inputs, _timeProvider.GetUtcNow()) with
            {
                Phase = _operations[0].Phase,
            };
            await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            return await ExecuteAsync(run, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<SetupRun> ResumeAsync(
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No setup run is available to resume.");
            if (run.Phase == SetupPhase.Completed)
            {
                return run;
            }

            return await ExecuteAsync(run, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public void Dispose()
    {
        _executionGate.Dispose();
    }

    private async Task<SetupRun> ExecuteAsync(
        SetupRun run,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startPhase = run.Phase == SetupPhase.Blocked
            ? run.Blocker?.Phase ?? throw new InvalidDataException("A blocked setup run has no blocker phase.")
            : run.Phase;
        var startIndex = FindOperationIndex(startPhase);

        for (var index = startIndex; index < _operations.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = _operations[index];
            run = Touch(
                run with
                {
                    Phase = operation.Phase,
                    Blocker = null,
                });
            await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SetupProgress(operation.Phase, operation.Status, index, _operations.Length));

            var context = new SetupExecutionContext(run);
            var check = await operation.CheckAsync(context, cancellationToken).ConfigureAwait(false);
            ValidateBlocker(operation.Phase, check.Blocker);
            if (check.Blocker is not null)
            {
                return await SaveBlockedAsync(run, check.Blocker, cancellationToken).ConfigureAwait(false);
            }

            if (!check.IsSatisfied)
            {
                run = Touch(run with { Attempt = checked(run.Attempt + 1) });
                await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
                context = new SetupExecutionContext(run);
                SetupOperationResult result;
                try
                {
                    result = await operation.ExecuteAsync(context, progress ?? NullProgress.Instance, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SetupOperationBlockedException exception)
                {
                    ValidateBlocker(operation.Phase, exception.Blocker);
                    return await SaveBlockedAsync(run, exception.Blocker, cancellationToken).ConfigureAwait(false);
                }

                ArgumentNullException.ThrowIfNull(result);
                ValidateBlocker(operation.Phase, result.Blocker);
                if (result.Disposition == SetupOperationDisposition.Blocked)
                {
                    var blocker = result.Blocker
                        ?? throw new InvalidDataException("A blocked setup operation did not provide a blocker.");
                    return await SaveBlockedAsync(run, blocker, cancellationToken).ConfigureAwait(false);
                }

                if (result.Blocker is not null)
                {
                    throw new InvalidDataException("A completed setup operation cannot provide a blocker.");
                }

                run = run with
                {
                    Inputs = result.UpdatedInputs ?? run.Inputs,
                    ImageBuildStagingDirectory =
                        result.ImageBuildStagingDirectory ?? run.ImageBuildStagingDirectory,
                };
            }

            var nextPhase = index + 1 < _operations.Length
                ? _operations[index + 1].Phase
                : SetupPhase.Completed;
            run = Touch(run with { Phase = nextPhase, Blocker = null });
            if (nextPhase == SetupPhase.Completed)
            {
                run = run with { Inputs = run.Inputs.RedactMediaPaths() };
            }

            await _store.SaveAsync(run, cancellationToken).ConfigureAwait(false);
            progress?.Report(
                new SetupProgress(
                    nextPhase,
                    nextPhase == SetupPhase.Completed ? "Windows 환경 준비가 완료되었습니다." : operation.Status,
                    index + 1,
                    _operations.Length));
        }

        return run;
    }

    private async Task<SetupRun> SaveBlockedAsync(
        SetupRun run,
        SetupBlocker blocker,
        CancellationToken cancellationToken)
    {
        var blocked = Touch(run with { Phase = SetupPhase.Blocked, Blocker = blocker });
        await _store.SaveAsync(blocked, cancellationToken).ConfigureAwait(false);
        return blocked;
    }

    private SetupRun Touch(SetupRun run) => run with { UpdatedAt = _timeProvider.GetUtcNow() };

    private int FindOperationIndex(SetupPhase phase)
    {
        for (var index = 0; index < _operations.Length; index++)
        {
            if (_operations[index].Phase == phase)
            {
                return index;
            }
        }

        throw new InvalidDataException($"Setup phase '{phase}' has no configured operation.");
    }

    private static ISetupOperation[] ValidateOperations(IEnumerable<ISetupOperation> operations)
    {
        var materialized = operations.ToArray();
        if (materialized.Length == 0)
        {
            throw new ArgumentException("At least one setup operation is required.", nameof(operations));
        }

        var previousIndex = -1;
        var phases = new HashSet<SetupPhase>();
        foreach (var operation in materialized)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var canonicalIndex = Array.IndexOf(CanonicalPhaseOrder, operation.Phase);
            if (canonicalIndex < 0 || canonicalIndex <= previousIndex || !phases.Add(operation.Phase))
            {
                throw new ArgumentException(
                    "Setup operations must use unique phases in canonical order.",
                    nameof(operations));
            }

            if (string.IsNullOrWhiteSpace(operation.Status))
            {
                throw new ArgumentException("Every setup operation requires a status message.", nameof(operations));
            }

            previousIndex = canonicalIndex;
        }

        return materialized;
    }

    private static void ValidateBlocker(SetupPhase phase, SetupBlocker? blocker)
    {
        if (blocker is not null && blocker.Phase != phase)
        {
            throw new InvalidDataException("A setup blocker must identify the operation that produced it.");
        }
    }

    private sealed class NullProgress : IProgress<SetupProgress>
    {
        public static NullProgress Instance { get; } = new();

        public void Report(SetupProgress value)
        {
            _ = value;
        }
    }
}
