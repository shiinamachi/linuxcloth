using System.Reflection;
using System.Text;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Cli;

public sealed class CliApplication
{
    private readonly TextWriter _error;
    private readonly TextWriter _output;
    private readonly ICliCommandServices _services;

    public CliApplication(
        ICliCommandServices services,
        TextWriter output,
        TextWriter error)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parsed = CliParser.Parse(arguments);
        if (!parsed.IsSuccess)
        {
            WriteError(parsed.Error!);
            WriteHelp(null, _error);
            return (int)CliExitCode.Usage;
        }

        try
        {
            return parsed.Command switch
            {
                HelpCommand command => ShowHelp(command),
                VersionCommand => ShowVersion(),
                DoctorCommand => await RunDoctorAsync(cancellationToken).ConfigureAwait(false),
                CatalogListCommand command => await RunCatalogAsync(
                        query: null,
                        command.Category,
                        command.CatalogRoot,
                        cancellationToken)
                    .ConfigureAwait(false),
                CatalogSearchCommand command => await RunCatalogAsync(
                        command.Query,
                        command.Category,
                        command.CatalogRoot,
                        cancellationToken)
                    .ConfigureAwait(false),
                ImageListCommand => await RunImageListAsync(cancellationToken).ConfigureAwait(false),
                ImageVerifyCommand command => await RunImageVerifyAsync(command, cancellationToken)
                    .ConfigureAwait(false),
                ImageBuildStartCommand command => await RunImageBuildStartAsync(command, cancellationToken)
                    .ConfigureAwait(false),
                ImageBuildResumeCommand command => await RunImageBuildResumeAsync(command, cancellationToken)
                    .ConfigureAwait(false),
                ImageBuildRecoverCommand command => await RunImageBuildRecoverAsync(command, cancellationToken)
                    .ConfigureAwait(false),
                CleanupCommand => await RunCleanupAsync(cancellationToken).ConfigureAwait(false),
                RunCommand command => await RunSessionAsync(command, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The parser returned an unsupported command."),
            };
        }
        catch (WindowsImageBuildCanceledException exception)
        {
            WriteError("Windows 이미지 빌드가 취소되었습니다. staging 데이터는 보존했습니다.");
            WriteStagingPath(exception.Staging.DirectoryPath, _error, preserved: true);
            return (int)CliExitCode.Cancelled;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _error.WriteLine("작업이 취소되었습니다.");
            return (int)CliExitCode.Cancelled;
        }
        catch (CliCommandException exception)
        {
            WriteError(exception.Message);
            return (int)exception.ExitCode;
        }
        catch (LaunchPrerequisiteException exception)
        {
            WriteError("호스트가 일회용 Windows VM 실행 요구사항을 충족하지 않습니다.");
            WriteFailedDoctorChecks(exception.Report);
            return (int)CliExitCode.HostUnavailable;
        }
        catch (ImageVerificationException exception)
        {
            WriteError("선택한 Windows 기준 이미지의 무결성 검증에 실패했습니다.");
            WriteVerificationIssues(exception.Result, _error);
            return (int)CliExitCode.IntegrityFailure;
        }
        catch (WindowsImageBuildException exception)
        {
            WriteError(exception.Message);
            if (exception.Staging is not null)
            {
                WriteStagingPath(exception.Staging.DirectoryPath, _error, preserved: true);
            }

            return (int)CliExitCode.ImageBuildFailure;
        }
        catch (CatalogBundleResolutionException exception)
        {
            WriteError(exception.Message);
            return (int)CliExitCode.NotFound;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
            DirectoryNotFoundException or
            KeyNotFoundException)
        {
            WriteError(exception.Message);
            return (int)CliExitCode.NotFound;
        }
        catch (Exception exception) when (
            exception is ImageRegistryException or
            CatalogValidationException or
            CatalogWorkspaceException)
        {
            WriteError(exception.Message);
            return (int)CliExitCode.IntegrityFailure;
        }
        catch (Exception exception)
        {
            WriteError($"예기치 않은 오류가 발생했습니다: {CliText.Sanitize(exception.Message)}");
            return (int)CliExitCode.SoftwareError;
        }
    }

    private int ShowHelp(HelpCommand command)
    {
        WriteHelp(command.Topic, _output);
        return (int)CliExitCode.Success;
    }

    private int ShowVersion()
    {
        var assembly = typeof(CliApplication).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                      assembly.GetName().Version?.ToString() ??
                      "unknown";
        _output.WriteLine($"linuxcloth {CliText.Sanitize(version)}");
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
    {
        var report = await _services.InspectHostAsync(cancellationToken).ConfigureAwait(false);
        foreach (var check in report.Checks)
        {
            var availability = check.IsAvailable ? "통과" : "실패";
            var requirement = check.IsRequired ? "필수" : "선택";
            var path = check.ResolvedPath is null
                ? string.Empty
                : $" ({CliText.Sanitize(check.ResolvedPath)})";
            _output.WriteLine(
                $"[{requirement}][{availability}] {CliText.Sanitize(check.Name)}{path}");
            if (!check.IsAvailable)
            {
                _output.WriteLine($"  {CliText.Sanitize(check.Detail)}");
            }
        }

        if (report.CanLaunch)
        {
            _output.WriteLine("일회용 Windows VM을 실행할 수 있습니다.");
            return (int)CliExitCode.Success;
        }

        _error.WriteLine("필수 호스트 검사를 통과하지 못했습니다.");
        return (int)CliExitCode.HostUnavailable;
    }

    private async Task<int> RunCatalogAsync(
        string? query,
        CatalogCategory? category,
        string? catalogRoot,
        CancellationToken cancellationToken)
    {
        var services = await _services.QueryCatalogAsync(
                query,
                category,
                catalogRoot,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var entry in services)
        {
            _output.WriteLine(
                $"{CliText.Sanitize(entry.Service.Id.Value)}\t" +
                $"{CliText.Sanitize(entry.Service.DisplayName)}\t" +
                $"{FormatCategory(entry.Service.Category)}\t" +
                FormatCompatibility(entry.Compatibility.Status));
        }

        _output.WriteLine($"총 {services.Count}개 서비스");
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunImageListAsync(CancellationToken cancellationToken)
    {
        var images = await _services.ListImagesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var image in images)
        {
            _output.WriteLine(
                $"{CliText.Sanitize(image.ImageId.Value)}\t" +
                $"{image.Metadata.CreatedAt:yyyy-MM-dd HH:mm:ss 'UTC'}\t" +
                $"{image.Metadata.MachineId:D}");
        }

        _output.WriteLine($"총 {images.Count}개 기준 이미지");
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunImageVerifyAsync(
        ImageVerifyCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _services.VerifyImageAsync(command.ImageId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsValid)
        {
            _output.WriteLine($"이미지 '{CliText.Sanitize(command.ImageId.Value)}' 무결성 검증을 통과했습니다.");
            return (int)CliExitCode.Success;
        }

        WriteVerificationIssues(result, _error);
        return (int)CliExitCode.IntegrityFailure;
    }

    private async Task<int> RunImageBuildStartAsync(
        ImageBuildStartCommand command,
        CancellationToken cancellationToken)
    {
        _output.WriteLine("사용자가 제공한 Windows와 virtio-win ISO를 검증한 뒤 격리된 설치 창을 엽니다.");
        _output.WriteLine("Windows 에디션과 설치 디스크를 선택하면 로컬 linuxcloth 계정, GuestBridge, virtio 드라이버를 자동 구성하고 종료합니다.");
        _output.WriteLine("설치 미디어가 없는 두 번째 부팅에서 GuestBridge 자기보고와 디스크 구조를 검사한 뒤에만 이미지를 봉인합니다.");
        _output.WriteLine("Windows 미디어와 생성한 이미지는 linuxcloth 배포물에 포함되지 않습니다.");
        var image = await _services.BuildImageAsync(
                command,
                CreateImageBuildProgress(),
                cancellationToken)
            .ConfigureAwait(false);
        WriteBuiltImage(image);
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunImageBuildResumeAsync(
        ImageBuildResumeCommand command,
        CancellationToken cancellationToken)
    {
        var image = await _services.ResumeImageBuildAsync(
                command,
                CreateImageBuildProgress(),
                cancellationToken)
            .ConfigureAwait(false);
        WriteBuiltImage(image);
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunImageBuildRecoverAsync(
        ImageBuildRecoverCommand command,
        CancellationToken cancellationToken)
    {
        _output.WriteLine("지속 기록된 PID, 부팅 ID, 시작 시각, 실행 파일 경로를 검증한 뒤 소유 프로세스만 복구합니다.");
        var workspace = await _services.RecoverImageBuildAsync(command, cancellationToken)
            .ConfigureAwait(false);
        _output.WriteLine($"중단된 이미지 빌드를 '{FormatImageBuildPhase(workspace.State.Phase)}' 상태로 복구했습니다.");
        WriteStagingPath(workspace.Staging.DirectoryPath, _output, preserved: true);
        _output.WriteLine("다음 명령으로 계속하세요: linuxcloth image build resume <IMAGE_ID> --staging <PATH>");
        return (int)CliExitCode.Success;
    }

    private async Task<int> RunCleanupAsync(CancellationToken cancellationToken)
    {
        var results = await _services.CleanupSessionsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var result in results)
        {
            var id = result.SessionId?.ToString("N") ?? Path.GetFileName(result.SessionDirectory);
            if (result.IsCleaned)
            {
                _output.WriteLine($"정리됨: {CliText.Sanitize(id)}");
            }
            else
            {
                _error.WriteLine(
                    $"보존됨: {CliText.Sanitize(id)} [{result.Disposition}] " +
                    CliText.Sanitize(result.Detail ?? "안전한 자동 정리를 완료하지 못했습니다."));
            }
        }

        var failed = results.Count(static result => !result.IsCleaned);
        _output.WriteLine($"정리 {results.Count - failed}개, 보존 {failed}개");
        return failed == 0
            ? (int)CliExitCode.Success
            : (int)CliExitCode.CleanupIncomplete;
    }

    private async Task<int> RunSessionAsync(
        RunCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClipboardEnabled)
        {
            _error.WriteLine("주의: 이 세션에서 호스트와 게스트 사이의 클립보드를 명시적으로 허용했습니다.");
        }

        var progress = new ImmediateProgress<SessionState>(state =>
            _output.WriteLine($"상태: {FormatSessionState(state)}"));
        var sessionId = await _services.RunSessionAsync(command, progress, cancellationToken)
            .ConfigureAwait(false);
        _output.WriteLine($"세션 {sessionId:N}이 종료되고 일회용 데이터가 정리되었습니다.");
        return (int)CliExitCode.Success;
    }

    private void WriteFailedDoctorChecks(DoctorReport report)
    {
        foreach (var check in report.Checks.Where(static check => check.IsRequired && !check.IsAvailable))
        {
            _error.WriteLine(
                $"- {CliText.Sanitize(check.Name)}: {CliText.Sanitize(check.Detail)}");
        }
    }

    private static void WriteVerificationIssues(
        ImageVerificationResult result,
        TextWriter writer)
    {
        writer.WriteLine($"이미지 '{CliText.Sanitize(result.ImageId.Value)}' 검증 실패:");
        foreach (var issue in result.Issues)
        {
            writer.WriteLine(
                $"- [{CliText.Sanitize(issue.Code)}] {CliText.Sanitize(issue.Artifact)}: " +
                CliText.Sanitize(issue.Message));
        }
    }

    private static string FormatCategory(CatalogCategory category) => category switch
    {
        CatalogCategory.Banking => "은행",
        CatalogCategory.Financing => "금융",
        CatalogCategory.Security => "증권",
        CatalogCategory.Insurance => "보험",
        CatalogCategory.CreditCard => "카드",
        CatalogCategory.Government => "정부",
        CatalogCategory.Education => "교육",
        _ => "기타",
    };

    private static string FormatCompatibility(CompatibilityStatus status) => status switch
    {
        CompatibilityStatus.Verified => "검증됨",
        CompatibilityStatus.Partial => "부분 지원",
        CompatibilityStatus.Blocked => "차단됨",
        _ => "미검증",
    };

    private static string FormatSessionState(SessionState state) => state switch
    {
        SessionState.Validating => "검사 중",
        SessionState.PreparingOverlay => "일회용 디스크 준비 중",
        SessionState.PreparingConfigDisk => "게스트 설정 준비 중",
        SessionState.StartingNetwork => "격리 네트워크 시작 중",
        SessionState.StartingVm => "Windows 시작 중",
        SessionState.WaitingForGuest => "Windows 준비 대기 중",
        SessionState.Running => "실행 중",
        SessionState.Stopping => "종료 중",
        SessionState.Cleaning => "일회용 데이터 정리 중",
        SessionState.Completed => "완료",
        SessionState.Failed => "실패",
        _ => "대기 중",
    };

    private ImmediateProgress<ImageBuildProgress> CreateImageBuildProgress() =>
        new ImmediateProgress<ImageBuildProgress>(progress =>
        {
            _output.WriteLine($"이미지 빌드 상태: {FormatImageBuildPhase(progress.Phase)}");
            if (progress.StagingDirectory is not null)
            {
                WriteStagingPath(progress.StagingDirectory, _output, preserved: false);
            }
        });

    private void WriteBuiltImage(ManagedWindowsImage image)
    {
        _output.WriteLine($"Windows 기준 이미지 '{CliText.Sanitize(image.ImageId.Value)}'를 봉인했습니다.");
        _output.WriteLine($"이미지 디렉터리: {CliText.Sanitize(image.DirectoryPath)}");
    }

    private static void WriteStagingPath(string path, TextWriter writer, bool preserved) =>
        writer.WriteLine(
            $"{(preserved ? "보존된 staging 경로" : "staging 경로")}: {CliText.Sanitize(path)}");

    private static string FormatImageBuildPhase(WindowsImageBuildPhase phase) => phase switch
    {
        WindowsImageBuildPhase.Preparing => "준비 중",
        WindowsImageBuildPhase.Prepared => "설치 준비 완료",
        WindowsImageBuildPhase.InstallerRunning => "Windows 설치 실행 중",
        WindowsImageBuildPhase.ReadyToVerify => "GuestBridge 검증 준비 완료",
        WindowsImageBuildPhase.VerificationRunning => "GuestBridge 및 Windows 환경 검증 중",
        WindowsImageBuildPhase.ReadyToFinalize => "봉인 준비 완료",
        _ => phase.ToString(),
    };

    private static void WriteHelp(string? topic, TextWriter writer)
    {
        if (topic is "run")
        {
            writer.WriteLine(
                "사용법: linuxcloth run <SERVICE_ID>... --image <IMAGE_ID> " +
                "[--cpus N] [--memory-mib N] [--no-network] [--enable-clipboard] [--catalog-root PATH]");
            return;
        }

        if (topic is "catalog" or "catalog list" or "catalog search")
        {
            writer.WriteLine("사용법:");
            writer.WriteLine("  linuxcloth catalog list [--category CATEGORY] [--catalog-root PATH]");
            writer.WriteLine("  linuxcloth catalog search <QUERY> [--category CATEGORY] [--catalog-root PATH]");
            return;
        }

        if (topic is "image" or "image list" or "image verify" or "image build")
        {
            writer.WriteLine("사용법:");
            writer.WriteLine("  linuxcloth image list");
            writer.WriteLine("  linuxcloth image verify <IMAGE_ID>");
            writer.WriteLine(
                "  linuxcloth image build start <IMAGE_ID> --windows-iso PATH --virtio-win-iso PATH --guest-bridge PATH " +
                "[--disk-gib N] [--cpus N] [--memory-mib N]");
            writer.WriteLine("  linuxcloth image build resume <IMAGE_ID> --staging PATH");
            writer.WriteLine("  linuxcloth image build recover <IMAGE_ID> --staging PATH");
            return;
        }

        writer.WriteLine("linuxcloth — 일회용 Windows 11 금융·공공 서비스 실행기");
        writer.WriteLine();
        writer.WriteLine("사용법: linuxcloth <명령> [옵션]");
        writer.WriteLine();
        writer.WriteLine("명령:");
        writer.WriteLine("  doctor                              호스트 실행 요구사항 검사");
        writer.WriteLine("  catalog list                        공식 카탈로그 서비스 목록");
        writer.WriteLine("  catalog search <QUERY>              공식 카탈로그 검색");
        writer.WriteLine("  image list                          관리 중인 기준 이미지 목록");
        writer.WriteLine("  image verify <IMAGE_ID>             기준 이미지 무결성 검사");
        writer.WriteLine("  image build start|resume|recover     사용자 ISO로 기준 이미지 생성");
        writer.WriteLine("  cleanup                             남은 세션의 안전한 복구·정리");
        writer.WriteLine("  run <SERVICE_ID>... --image <ID>    일회용 Windows 세션 실행");
        writer.WriteLine();
        writer.WriteLine("공통 옵션: --help, --version");
        writer.WriteLine($"카탈로그 루트 환경 변수: {CatalogBundleResolver.CatalogRootEnvironmentVariable}");
    }

    private void WriteError(string message) =>
        _error.WriteLine($"오류: {CliText.Sanitize(message)}");

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public ImmediateProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }
}

internal static class CliText
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        const int maximumLength = 1024;
        var builder = new StringBuilder(Math.Min(value.Length, maximumLength));
        foreach (var character in value.Take(maximumLength))
        {
            builder.Append(char.IsControl(character) ? '\uFFFD' : character);
        }

        if (value.Length > maximumLength)
        {
            builder.Append('…');
        }

        return builder.ToString();
    }
}
