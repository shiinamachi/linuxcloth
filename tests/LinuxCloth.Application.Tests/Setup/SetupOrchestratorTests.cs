using LinuxCloth.Application.Images;
using LinuxCloth.Application.Setup;

namespace LinuxCloth.Application.Tests.Setup;

public sealed class SetupOrchestratorTests
{
    [Fact]
    public async Task ExecutesOperationsDurablyAndRedactsMediaPathsOnCompletion()
    {
        var store = new MemorySetupRunStore();
        var operations = new[]
        {
            new FakeOperation(SetupPhase.Discovering),
            new FakeOperation(SetupPhase.ValidatingMedia),
            new FakeOperation(SetupPhase.BuildingImage)
            {
                Result = SetupOperationResult.Completed(imageBuildStagingDirectory: "/data/staging/build"),
            },
            new FakeOperation(SetupPhase.Finalizing),
        };
        using var orchestrator = new SetupOrchestrator(store, operations);

        var completed = await orchestrator.StartAsync(CreateInputs());

        Assert.Equal(SetupPhase.Completed, completed.Phase);
        Assert.Null(completed.Inputs.WindowsIsoPath);
        Assert.Null(completed.Inputs.VirtioIsoPath);
        Assert.Equal("/data/staging/build", completed.ImageBuildStagingDirectory);
        Assert.Equal(4, completed.Attempt);
        Assert.All(operations, operation => Assert.Equal(1, operation.ExecuteCount));
        Assert.Contains(store.Saves, run => run.Phase == SetupPhase.BuildingImage);
        Assert.Contains(store.Saves, run => run.Phase == SetupPhase.Completed);
    }

    [Fact]
    public async Task ResumeRechecksAndSkipsAlreadySatisfiedOperations()
    {
        var store = new MemorySetupRunStore();
        var discovering = new FakeOperation(SetupPhase.Discovering) { IsSatisfied = true };
        var building = new FakeOperation(SetupPhase.BuildingImage)
        {
            Failure = new IOException("temporary failure"),
        };
        var finalizing = new FakeOperation(SetupPhase.Finalizing);
        using var orchestrator = new SetupOrchestrator(store, [discovering, building, finalizing]);

        await Assert.ThrowsAsync<IOException>(() => orchestrator.StartAsync(CreateInputs()));
        Assert.Equal(SetupPhase.BuildingImage, store.Run?.Phase);
        building.Failure = null;
        building.IsSatisfied = true;

        var completed = await orchestrator.ResumeAsync();

        Assert.Equal(SetupPhase.Completed, completed.Phase);
        Assert.Equal(0, discovering.ExecuteCount);
        Assert.Equal(1, building.ExecuteCount);
        Assert.Equal(1, finalizing.ExecuteCount);
    }

    [Fact]
    public async Task BlockedOperationResumesAtProducingPhase()
    {
        var store = new MemorySetupRunStore();
        var operation = new FakeOperation(SetupPhase.AwaitingInputs)
        {
            Check = new SetupOperationCheck(
                false,
                new SetupBlocker(
                    SetupPhase.AwaitingInputs,
                    SetupFailureKind.UserActionRequired,
                    "SETUP-MEDIA-WINDOWS-MISSING",
                    "Windows 설치 파일이 필요합니다",
                    "Windows 11 설치 파일을 선택하세요.",
                    "파일 선택")),
        };
        using var orchestrator = new SetupOrchestrator(store, [operation]);

        var blocked = await orchestrator.StartAsync(CreateInputs());

        Assert.Equal(SetupPhase.Blocked, blocked.Phase);
        Assert.Equal("SETUP-MEDIA-WINDOWS-MISSING", blocked.Blocker?.Code);
        operation.Check = SetupOperationCheck.Required;
        var completed = await orchestrator.ResumeAsync();
        Assert.Equal(SetupPhase.Completed, completed.Phase);
        Assert.Equal(1, operation.ExecuteCount);
    }

    [Fact]
    public void RejectsOutOfOrderOrDuplicateOperations()
    {
        var store = new MemorySetupRunStore();

        Assert.Throws<ArgumentException>(
            () => new SetupOrchestrator(
                store,
                [new FakeOperation(SetupPhase.BuildingImage), new FakeOperation(SetupPhase.ValidatingMedia)]));
        Assert.Throws<ArgumentException>(
            () => new SetupOrchestrator(
                store,
                [new FakeOperation(SetupPhase.Discovering), new FakeOperation(SetupPhase.Discovering)]));
    }

    private static SetupInputSnapshot CreateInputs() => new(
        "/media/windows.iso",
        null,
        "/media/virtio.iso",
        null,
        null,
        null,
        null,
        ImageId.Parse("windows-11"),
        80,
        4,
        8192,
        LicenseConfirmed: true);

    private sealed class FakeOperation : ISetupOperation
    {
        public FakeOperation(SetupPhase phase)
        {
            Phase = phase;
        }

        public SetupPhase Phase { get; }

        public string Status => Phase.ToString();

        public bool IsSatisfied { get; set; }

        public SetupOperationCheck? Check { get; set; }

        public SetupOperationResult Result { get; set; } = SetupOperationResult.Completed();

        public Exception? Failure { get; set; }

        public int ExecuteCount { get; private set; }

        public Task<SetupOperationCheck> CheckAsync(
            SetupExecutionContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Check ?? new SetupOperationCheck(IsSatisfied));
        }

        public Task<SetupOperationResult> ExecuteAsync(
            SetupExecutionContext context,
            IProgress<SetupProgress> progress,
            CancellationToken cancellationToken)
        {
            _ = context;
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Failure is null
                ? Task.FromResult(Result)
                : Task.FromException<SetupOperationResult>(Failure);
        }
    }

    private sealed class MemorySetupRunStore : ISetupRunStore
    {
        public List<SetupRun> Saves { get; } = [];

        public SetupRun? Run { get; private set; }

        public Task<SetupRun?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Run);
        }

        public Task SaveAsync(SetupRun run, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run = run;
            Saves.Add(run);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Run = null;
            return Task.CompletedTask;
        }
    }
}
