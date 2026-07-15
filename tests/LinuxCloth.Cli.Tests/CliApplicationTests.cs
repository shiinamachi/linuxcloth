using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task ParseFailureReturnsUsageWithoutCallingServices()
    {
        var services = new FakeCliCommandServices();
        var (exitCode, _, error) = await RunAsync(services, ["run", "WooriBank"]);

        Assert.Equal((int)CliExitCode.Usage, exitCode);
        Assert.Contains("--image", error, StringComparison.Ordinal);
        Assert.Equal(0, services.CallCount);
    }

    [Fact]
    public async Task DoctorFailureReturnsHostUnavailable()
    {
        var services = new FakeCliCommandServices
        {
            DoctorReport = new DoctorReport(
            [
                new DoctorCheck("kvm", true, false, "missing"),
                new DoctorCheck("xorriso", false, false, "optional"),
            ]),
        };

        var (exitCode, output, error) = await RunAsync(services, ["doctor"]);

        Assert.Equal((int)CliExitCode.HostUnavailable, exitCode);
        Assert.Contains("[필수][실패] kvm", output, StringComparison.Ordinal);
        Assert.Contains("필수 호스트 검사", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalDoctorFailureDoesNotBlockLaunchReadiness()
    {
        var services = new FakeCliCommandServices
        {
            DoctorReport = new DoctorReport(
            [
                new DoctorCheck("kvm", true, true, "ok", "/dev/kvm"),
                new DoctorCheck("xorriso", false, false, "optional"),
            ]),
        };

        var (exitCode, output, _) = await RunAsync(services, ["doctor"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.Contains("실행할 수 있습니다", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidImageReturnsIntegrityFailure()
    {
        var services = new FakeCliCommandServices
        {
            VerificationResult = new ImageVerificationResult(
                ImageId.Parse("win11"),
                [new ImageVerificationIssue("hash-mismatch", "baseImage", "changed")]),
        };

        var (exitCode, _, error) = await RunAsync(
            services,
            ["image", "verify", "win11"]);

        Assert.Equal((int)CliExitCode.IntegrityFailure, exitCode);
        Assert.Contains("hash-mismatch", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservedRecoveryEntryReturnsCleanupIncomplete()
    {
        var sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var services = new FakeCliCommandServices
        {
            RecoveryResults =
            [
                new RecoveryResult(
                    $"/run/linuxcloth/sessions/{sessionId:N}",
                    sessionId,
                    RecoveryDisposition.PreservedIdentityMismatch,
                    Detail: "identity mismatch"),
            ],
        };

        var (exitCode, output, error) = await RunAsync(services, ["cleanup"]);

        Assert.Equal((int)CliExitCode.CleanupIncomplete, exitCode);
        Assert.Contains("보존 1개", output, StringComparison.Ordinal);
        Assert.Contains("보존됨", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAwaitsServiceAndReportsState()
    {
        var services = new FakeCliCommandServices
        {
            SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        };

        var (exitCode, output, _) = await RunAsync(
            services,
            ["run", "WooriBank", "--image", "win11"]);

        Assert.Equal((int)CliExitCode.Success, exitCode);
        Assert.Contains("상태: 실행 중", output, StringComparison.Ordinal);
        Assert.Contains(services.SessionId.ToString("N"), output, StringComparison.Ordinal);
        Assert.NotNull(services.LastRunCommand);
    }

    [Fact]
    public async Task CancellationReturnsShellCancellationCode()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var services = new FakeCliCommandServices { ThrowCancellation = true };
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new CliApplication(services, output, error);

        var exitCode = await application.RunAsync(["doctor"], cancellation.Token);

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);
        Assert.Contains("취소", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandExceptionUsesDeclaredExitCode()
    {
        var services = new FakeCliCommandServices
        {
            Failure = new CliCommandException(CliExitCode.NotFound, "없음"),
        };

        var (exitCode, _, error) = await RunAsync(services, ["image", "list"]);

        Assert.Equal((int)CliExitCode.NotFound, exitCode);
        Assert.Contains("없음", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageBuildFailureUsesDedicatedExitCodeAndShowsEveryPhase()
    {
        var services = new FakeCliCommandServices
        {
            Failure = new WindowsImageBuildException("installer failed"),
            BuildPhases =
            [
                WindowsImageBuildPhase.Preparing,
                WindowsImageBuildPhase.Prepared,
                WindowsImageBuildPhase.InstallerRunning,
                WindowsImageBuildPhase.ReadyToVerify,
                WindowsImageBuildPhase.VerificationRunning,
                WindowsImageBuildPhase.ReadyToFinalize,
            ],
        };

        var (exitCode, output, error) = await RunAsync(
            services,
        [
            "image", "build", "start", "win11",
            "--windows-iso", "/media/windows.iso",
            "--virtio-win-iso", "/media/virtio.iso",
            "--guest-bridge", "/opt/linuxcloth/linuxcloth-guest-bridge.exe",
        ]);

        Assert.Equal((int)CliExitCode.ImageBuildFailure, exitCode);
        Assert.Contains("installer failed", error, StringComparison.Ordinal);
        Assert.Contains("준비 중", output, StringComparison.Ordinal);
        Assert.Contains("설치 준비 완료", output, StringComparison.Ordinal);
        Assert.Contains("Windows 설치 실행 중", output, StringComparison.Ordinal);
        Assert.Contains("GuestBridge 검증 준비 완료", output, StringComparison.Ordinal);
        Assert.Contains("GuestBridge 및 Windows 환경 검증 중", output, StringComparison.Ordinal);
        Assert.Contains("봉인 준비 완료", output, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        FakeCliCommandServices services,
        IReadOnlyList<string> arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = new CliApplication(services, output, error);
        var exitCode = await application.RunAsync(arguments);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class FakeCliCommandServices : ICliCommandServices
    {
        public DoctorReport DoctorReport { get; init; } = new(
            [new DoctorCheck("kvm", true, true, "ok", "/dev/kvm")]);

        public ImageVerificationResult VerificationResult { get; init; } = new(
            ImageId.Parse("win11"),
            []);

        public IReadOnlyList<RecoveryResult> RecoveryResults { get; init; } = [];

        public IReadOnlyList<WindowsImageBuildPhase> BuildPhases { get; init; } = [];

        public Guid SessionId { get; init; } = Guid.NewGuid();

        public Exception? Failure { get; init; }

        public bool ThrowCancellation { get; init; }

        public int CallCount { get; private set; }

        public RunCommand? LastRunCommand { get; private set; }

        public Task<DoctorReport> InspectHostAsync(CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromResult(DoctorReport);
        }

        public Task<IReadOnlyList<CatalogServiceEntry>> QueryCatalogAsync(
            string? query,
            CatalogCategory? category,
            string? catalogRoot,
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromResult<IReadOnlyList<CatalogServiceEntry>>([]);
        }

        public Task<IReadOnlyList<ManagedWindowsImage>> ListImagesAsync(
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromResult<IReadOnlyList<ManagedWindowsImage>>([]);
        }

        public Task<ImageVerificationResult> VerifyImageAsync(
            ImageId imageId,
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromResult(VerificationResult);
        }

        public Task<ManagedWindowsImage> BuildImageAsync(
            ImageBuildStartCommand command,
            IProgress<ImageBuildProgress> progress,
            CancellationToken cancellationToken)
        {
            foreach (var phase in BuildPhases)
            {
                progress.Report(new ImageBuildProgress(phase, null));
            }

            BeforeCall(cancellationToken);
            return Task.FromException<ManagedWindowsImage>(
                new InvalidOperationException("No fake build result was configured."));
        }

        public Task<ManagedWindowsImage> ResumeImageBuildAsync(
            ImageBuildResumeCommand command,
            IProgress<ImageBuildProgress> progress,
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromException<ManagedWindowsImage>(
                new InvalidOperationException("No fake resume result was configured."));
        }

        public Task<WindowsImageBuildWorkspace> RecoverImageBuildAsync(
            ImageBuildRecoverCommand command,
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromException<WindowsImageBuildWorkspace>(
                new InvalidOperationException("No fake recovery result was configured."));
        }

        public Task<IReadOnlyList<RecoveryResult>> CleanupSessionsAsync(
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            return Task.FromResult(RecoveryResults);
        }

        public Task<Guid> RunSessionAsync(
            RunCommand command,
            IProgress<SessionState> progress,
            CancellationToken cancellationToken)
        {
            BeforeCall(cancellationToken);
            LastRunCommand = command;
            progress.Report(SessionState.Running);
            return Task.FromResult(SessionId);
        }

        private void BeforeCall(CancellationToken cancellationToken)
        {
            CallCount++;
            if (ThrowCancellation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
