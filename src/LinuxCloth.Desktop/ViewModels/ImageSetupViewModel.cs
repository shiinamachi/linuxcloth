using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Services;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class ImageSetupViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CancellationToken _applicationShutdown;
    private readonly IDesktopImageBuildService _imageBuildService;
    private readonly Func<CancellationToken, Task> _onImageRegistered;
    private TaskCompletionSource? _operationFinished;
    private CancellationTokenSource? _operationCancellation;
    private string _buildStatus = "설치 미디어와 펌웨어를 선택하세요.";
    private int _cpuCount = 4;
    private int _diskSizeGiB = 96;
    private string? _errorMessage;
    private string _guestBridgeExecutablePath = string.Empty;
    private string _imageIdText = "windows-11";
    private bool _isBuilding;
    private bool _isInitialized;
    private int _memoryMiB = 6144;
    private string _ovmfCodePath = string.Empty;
    private string _ovmfVariablesTemplatePath = string.Empty;
    private string _stagingDirectory = string.Empty;
    private string _virtioWinIsoPath = string.Empty;
    private string _windowsIsoPath = string.Empty;

    public ImageSetupViewModel(
        IDesktopImageBuildService imageBuildService,
        Func<CancellationToken, Task> onImageRegistered,
        CancellationToken applicationShutdown = default)
    {
        _imageBuildService = imageBuildService ?? throw new ArgumentNullException(nameof(imageBuildService));
        _onImageRegistered = onImageRegistered ?? throw new ArgumentNullException(nameof(onImageRegistered));
        _applicationShutdown = applicationShutdown;
        StartBuildCommand = new AsyncCommand(StartBuildAsync, () => CanStartBuild, ShowError);
        ResumeBuildCommand = new AsyncCommand(ResumeBuildAsync, () => CanResumeBuild, ShowError);
        CancelBuildCommand = new AsyncCommand(
            CancelBuildAsync,
            () => IsBuilding,
            ShowError);
    }

    public AsyncCommand StartBuildCommand { get; }

    public AsyncCommand ResumeBuildCommand { get; }

    public AsyncCommand CancelBuildCommand { get; }

    public string ImageIdText
    {
        get => _imageIdText;
        set
        {
            if (IsBuilding)
            {
                return;
            }

            if (SetProperty(ref _imageIdText, value?.Trim() ?? string.Empty))
            {
                RaiseCommandState();
            }
        }
    }

    public string WindowsIsoPath
    {
        get => _windowsIsoPath;
        set => SetRequiredPath(ref _windowsIsoPath, value);
    }

    public string VirtioWinIsoPath
    {
        get => _virtioWinIsoPath;
        set => SetRequiredPath(ref _virtioWinIsoPath, value);
    }

    public string GuestBridgeExecutablePath
    {
        get => _guestBridgeExecutablePath;
        set => SetRequiredPath(ref _guestBridgeExecutablePath, value);
    }

    public string OvmfCodePath
    {
        get => _ovmfCodePath;
        set => SetRequiredPath(ref _ovmfCodePath, value);
    }

    public string OvmfVariablesTemplatePath
    {
        get => _ovmfVariablesTemplatePath;
        set => SetRequiredPath(ref _ovmfVariablesTemplatePath, value);
    }

    public string StagingDirectory
    {
        get => _stagingDirectory;
        set
        {
            if (SetProperty(ref _stagingDirectory, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasStagingDirectory));
                RaiseCommandState();
            }
        }
    }

    public int DiskSizeGiB
    {
        get => _diskSizeGiB;
        set
        {
            if (IsBuilding)
            {
                return;
            }

            if (SetProperty(ref _diskSizeGiB, value))
            {
                RaiseCommandState();
            }
        }
    }

    public int CpuCount
    {
        get => _cpuCount;
        set
        {
            if (IsBuilding)
            {
                return;
            }

            if (SetProperty(ref _cpuCount, value))
            {
                RaiseCommandState();
            }
        }
    }

    public int MemoryMiB
    {
        get => _memoryMiB;
        set
        {
            if (IsBuilding)
            {
                return;
            }

            if (SetProperty(ref _memoryMiB, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool CanStartBuild =>
        !IsBuilding &&
        ImageId.TryParse(ImageIdText, out _) &&
        RequiredPathsArePresent() &&
        DiskSizeGiB is >= 64 and <= 1024 &&
        CpuCount is >= 2 and <= 32 &&
        MemoryMiB is >= 4096 and <= 131072;

    public bool CanResumeBuild =>
        !IsBuilding &&
        ImageId.TryParse(ImageIdText, out _) &&
        !string.IsNullOrWhiteSpace(StagingDirectory);

    public bool HasStagingDirectory => !string.IsNullOrWhiteSpace(StagingDirectory);

    public string BuildStatus
    {
        get => _buildStatus;
        private set => SetProperty(ref _buildStatus, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        try
        {
            var defaults = await _imageBuildService
                .GetImageBuildDefaultsAsync(_applicationShutdown)
                .ConfigureAwait(true);
            GuestBridgeExecutablePath = defaults.GuestBridgeExecutablePath;
            OvmfCodePath = defaults.OvmfCodePath ?? string.Empty;
            OvmfVariablesTemplatePath = defaults.OvmfVariablesTemplatePath ?? string.Empty;
            BuildStatus = !defaults.IsGuestBridgeAvailable
                ? "앱과 함께 설치된 GuestBridge를 찾지 못했습니다. GuestBridge 실행 파일을 직접 선택하세요."
                : defaults.OvmfCodePath is null || defaults.OvmfVariablesTemplatePath is null
                    ? "검증된 Secure Boot OVMF 디스크립터를 찾지 못했습니다. 배포판의 QEMU 펌웨어 패키지를 설치하세요."
                    : "감지된 OVMF 경로를 확인하고 Windows 설치 미디어를 선택하세요.";
        }
        catch (OperationCanceledException) when (_applicationShutdown.IsCancellationRequested)
        {
            BuildStatus = "앱 종료로 이미지 설정 준비가 취소되었습니다.";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public void ReportPickerError(Exception exception) => ShowError(exception);

    public async Task CancelAndWaitAsync()
    {
        _operationCancellation?.Cancel();
        var operationFinished = _operationFinished;
        if (operationFinished is null)
        {
            return;
        }

        try
        {
            await operationFinished.Task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The application shutdown token can cancel before the operation starts.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CancelAndWaitAsync().ConfigureAwait(true);
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        GC.SuppressFinalize(this);
    }

    public async Task StartBuildAsync()
    {
        if (!CanStartBuild)
        {
            throw new InvalidOperationException(
                IsBuilding
                    ? "이미지 생성 작업이 이미 실행 중입니다."
                    : "새 기준 이미지 생성에 필요한 입력값을 모두 확인하세요.");
        }

        var request = new DesktopImageBuildRequest(
            ImageId.Parse(ImageIdText),
            RequireAbsolutePath(WindowsIsoPath, "Windows ISO"),
            RequireAbsolutePath(VirtioWinIsoPath, "virtio-win ISO"),
            RequireAbsolutePath(GuestBridgeExecutablePath, "GuestBridge"),
            RequireAbsolutePath(OvmfCodePath, "OVMF 코드"),
            RequireAbsolutePath(OvmfVariablesTemplatePath, "OVMF 변수 템플릿"),
            DiskSizeGiB,
            CpuCount,
            MemoryMiB);
        await RunBuildOperationAsync(
            (progress, cancellationToken) => _imageBuildService.BuildImageAsync(
                request,
                progress,
                cancellationToken)).ConfigureAwait(true);
    }

    public async Task ResumeBuildAsync()
    {
        if (!CanResumeBuild)
        {
            throw new InvalidOperationException(
                IsBuilding
                    ? "이미지 생성 작업이 이미 실행 중입니다."
                    : "재개할 이미지 ID와 스테이징 디렉터리를 확인하세요.");
        }

        var imageId = ImageId.Parse(ImageIdText);
        var stagingDirectory = RequireAbsolutePath(StagingDirectory, "스테이징 디렉터리");
        await RunBuildOperationAsync(
            (progress, cancellationToken) => _imageBuildService.ResumeImageBuildAsync(
                imageId,
                stagingDirectory,
                progress,
                cancellationToken)).ConfigureAwait(true);
    }

    public Task CancelBuildAsync()
    {
        BuildStatus = "현재 단계를 안전하게 중단하고 재개 상태를 보존하고 있습니다…";
        _operationCancellation?.Cancel();
        return Task.CompletedTask;
    }

    private async Task RunBuildOperationAsync(
        Func<IProgress<DesktopImageBuildProgress>, CancellationToken, Task<ManagedWindowsImage>> operation)
    {
        ErrorMessage = null;
        IsBuilding = true;
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationShutdown);
        var operationFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _operationFinished = operationFinished;
        var cancellationToken = _operationCancellation.Token;
        var progressLifetime = new ProgressLifetime();
        var progress = new Progress<DesktopImageBuildProgress>(value =>
        {
            if (progressLifetime.IsActive)
            {
                ApplyProgress(value);
            }
        });
        var task = operation(progress, cancellationToken);
        try
        {
            ManagedWindowsImage image;
            try
            {
                image = await task.ConfigureAwait(true);
            }
            finally
            {
                progressLifetime.Stop();
            }

            StagingDirectory = string.Empty;
            BuildStatus = $"기준 이미지 '{image.ImageId.Value}' 등록을 완료했습니다.";
            await _onImageRegistered(CancellationToken.None).ConfigureAwait(true);
        }
        catch (WindowsImageBuildCanceledException exception)
        {
            StagingDirectory = exception.Staging.DirectoryPath;
            BuildStatus = "이미지 생성을 취소했습니다. 아래 스테이징 경로에서 다시 시작할 수 있습니다.";
        }
        catch (OperationCanceledException)
        {
            BuildStatus = HasStagingDirectory
                ? "이미지 생성을 취소했습니다. 보존된 스테이징 경로에서 다시 시작할 수 있습니다."
                : "이미지 생성을 취소했습니다.";
        }
        catch (WindowsImageBuildException exception)
        {
            if (exception.Staging is not null)
            {
                StagingDirectory = exception.Staging.DirectoryPath;
            }

            ErrorMessage = HasStagingDirectory
                ? "이미지 생성에 실패했습니다. 스테이징 데이터는 보존되었으며 다시 시작할 수 있습니다."
                : "이미지 생성 준비에 실패했습니다. 선택한 파일과 시스템 검사 결과를 확인하세요.";
            BuildStatus = "이미지 생성을 완료하지 못했습니다.";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            progressLifetime.Stop();
            IsBuilding = false;
            operationFinished.TrySetResult();
            if (ReferenceEquals(_operationFinished, operationFinished))
            {
                _operationFinished = null;
            }
        }
    }

    private void ApplyProgress(DesktopImageBuildProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.StagingDirectory))
        {
            StagingDirectory = progress.StagingDirectory;
        }

        BuildStatus = progress.IsRecovery
            ? "중단된 프로세스를 확인하고 안전한 재개 지점으로 복구하고 있습니다…"
            : PhaseText(progress.Phase);
    }

    private void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ErrorMessage = exception switch
        {
            FormatException => "이미지 ID는 영문 소문자, 숫자, 내부 하이픈만 사용할 수 있습니다.",
            FileNotFoundException => "선택한 파일을 찾을 수 없습니다.",
            UnauthorizedAccessException => "선택한 경로에 접근할 권한이 없습니다.",
            OperationCanceledException => "작업이 취소되었습니다.",
            InvalidOperationException or ArgumentException => exception.Message,
            _ => "이미지 설정 작업을 완료하지 못했습니다. 입력 경로와 시스템 요구사항을 확인하세요.",
        };
        BuildStatus = "이미지 설정 작업을 완료하지 못했습니다.";
    }

    private bool RequiredPathsArePresent() =>
        !string.IsNullOrWhiteSpace(WindowsIsoPath) &&
        !string.IsNullOrWhiteSpace(VirtioWinIsoPath) &&
        !string.IsNullOrWhiteSpace(GuestBridgeExecutablePath) &&
        !string.IsNullOrWhiteSpace(OvmfCodePath) &&
        !string.IsNullOrWhiteSpace(OvmfVariablesTemplatePath);

    private void SetRequiredPath(ref string field, string? value)
    {
        if (IsBuilding)
        {
            return;
        }

        if (SetProperty(ref field, value?.Trim() ?? string.Empty))
        {
            RaiseCommandState();
        }
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanStartBuild));
        OnPropertyChanged(nameof(CanResumeBuild));
        StartBuildCommand.RaiseCanExecuteChanged();
        ResumeBuildCommand.RaiseCanExecuteChanged();
        CancelBuildCommand.RaiseCanExecuteChanged();
    }

    private static string RequireAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{label} 경로는 절대 경로여야 합니다.");
        }

        return Path.GetFullPath(value);
    }

    private static string PhaseText(WindowsImageBuildPhase phase) => phase switch
    {
        WindowsImageBuildPhase.Preparing => "설치 미디어를 검증하고 이미지 작업 공간을 만들고 있습니다…",
        WindowsImageBuildPhase.Prepared => "Windows 설치를 시작할 준비가 되었습니다.",
        WindowsImageBuildPhase.InstallerRunning => "Windows를 설치하고 GuestBridge를 구성하고 있습니다. 열린 Windows 창을 닫지 마세요.",
        WindowsImageBuildPhase.ReadyToVerify => "설치가 끝났습니다. 미디어 없는 검증 부팅을 준비하고 있습니다…",
        WindowsImageBuildPhase.VerificationRunning => "GuestBridge와 Windows 환경을 검증하고 있습니다…",
        WindowsImageBuildPhase.ReadyToFinalize => "검증을 마쳤습니다. 기준 이미지를 봉인하고 있습니다…",
        _ => "이미지 생성을 준비하고 있습니다…",
    };

    private sealed class ProgressLifetime
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) == 1;

        public void Stop() => Interlocked.Exchange(ref _active, 0);
    }
}
