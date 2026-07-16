using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class ImageSetupViewModel : ObservableObject, INotifyDataErrorInfo, IAsyncDisposable
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
    private HostCapacitySnapshot _hostCapacity = HostCapacitySnapshot.Unknown;

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

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

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
                RaiseValidationChanged(nameof(ImageIdText));
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
                RaiseValidationChanged(nameof(StagingDirectory));
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
                RaiseValidationChanged(nameof(DiskSizeGiB));
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
                RaiseValidationChanged(nameof(CpuCount));
                RaiseResourceWarning();
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
                RaiseValidationChanged(nameof(MemoryMiB));
                RaiseResourceWarning();
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

    public bool HasErrors => GetValidationErrors(null).Count > 0;

    public IEnumerable GetErrors(string? propertyName) => GetValidationErrors(propertyName);

    public string ResourceWarning
    {
        get
        {
            var warnings = new List<string>();
            if (_hostCapacity.LogicalProcessorCount > 0 && CpuCount > _hostCapacity.LogicalProcessorCount)
            {
                warnings.Add($"가상 CPU가 호스트 논리 CPU {_hostCapacity.LogicalProcessorCount}개보다 많습니다.");
            }

            if (_hostCapacity.AvailableMemoryBytes > 0 &&
                (long)MemoryMiB * 1024 * 1024 > _hostCapacity.AvailableMemoryBytes * 3 / 4)
            {
                warnings.Add("가상 메모리가 호스트에서 사용 가능한 메모리의 75%를 초과합니다.");
            }

            return string.Join(' ', warnings);
        }
    }

    public bool HasResourceWarning => ResourceWarning.Length > 0;

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
            ApplyDefaults(defaults);
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

    public void ApplyDefaults(DesktopImageBuildDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        GuestBridgeExecutablePath = defaults.GuestBridgeExecutablePath;
        OvmfCodePath = defaults.OvmfCodePath ?? string.Empty;
        OvmfVariablesTemplatePath = defaults.OvmfVariablesTemplatePath ?? string.Empty;
        BuildStatus = !defaults.IsGuestBridgeAvailable
            ? "Windows 연결 구성 요소가 없습니다. linuxcloth 패키지를 다시 설치하세요."
            : defaults.OvmfCodePath is null || defaults.OvmfVariablesTemplatePath is null
                ? "Windows 시작 구성 요소를 찾지 못했습니다. 필수 구성 요소를 설치하고 다시 확인하세요."
                : "Windows 연결 및 시작 구성 요소를 확인했습니다.";
    }

    public void ApplyHostCapacity(HostCapacitySnapshot capacity)
    {
        _hostCapacity = capacity ?? throw new ArgumentNullException(nameof(capacity));
        RaiseResourceWarning();
    }

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
                    : "새 Windows 환경에 필요한 입력값을 모두 확인하세요.");
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
                    : "계속할 이전 작업 정보를 확인하세요.");
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
            BuildStatus = $"Windows 환경 '{image.ImageId.Value}' 준비를 마쳤습니다.";
            await _onImageRegistered(CancellationToken.None).ConfigureAwait(true);
        }
        catch (WindowsImageBuildCanceledException exception)
        {
            StagingDirectory = exception.Staging.DirectoryPath;
            BuildStatus = "Windows 환경 만들기를 취소했습니다. 보존된 작업 정보로 다시 시작할 수 있습니다.";
        }
        catch (OperationCanceledException)
        {
            BuildStatus = HasStagingDirectory
                ? "Windows 환경 만들기를 취소했습니다. 보존된 작업 정보로 다시 시작할 수 있습니다."
                : "이미지 생성을 취소했습니다.";
        }
        catch (WindowsImageBuildException exception)
        {
            if (exception.Staging is not null)
            {
                StagingDirectory = exception.Staging.DirectoryPath;
            }

            ErrorMessage = HasStagingDirectory
                ? "Windows 환경을 만들지 못했습니다. 작업 정보는 보존되어 다시 시작할 수 있습니다."
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
        Trace.TraceError("Windows environment build failed: {0}", exception);
        ErrorMessage = exception switch
        {
            FormatException => "환경 이름은 영문 소문자, 숫자, 내부 하이픈만 사용할 수 있습니다.",
            FileNotFoundException => "선택한 파일을 찾을 수 없습니다.",
            UnauthorizedAccessException => "선택한 경로에 접근할 권한이 없습니다.",
            OperationCanceledException => "작업이 취소되었습니다.",
            _ => "Windows 환경을 만들지 못했습니다. 입력 파일과 시스템 요구사항을 확인하세요.",
        };
        BuildStatus = "Windows 환경 준비를 완료하지 못했습니다.";
    }

    private bool RequiredPathsArePresent() =>
        !string.IsNullOrWhiteSpace(WindowsIsoPath) &&
        !string.IsNullOrWhiteSpace(VirtioWinIsoPath) &&
        !string.IsNullOrWhiteSpace(GuestBridgeExecutablePath) &&
        !string.IsNullOrWhiteSpace(OvmfCodePath) &&
        !string.IsNullOrWhiteSpace(OvmfVariablesTemplatePath);

    private void SetRequiredPath(
        ref string field,
        string? value,
        [CallerMemberName] string? propertyName = null)
    {
        if (IsBuilding)
        {
            return;
        }

        if (SetProperty(ref field, value?.Trim() ?? string.Empty, propertyName))
        {
            RaiseValidationChanged(propertyName);
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

    private void RaiseResourceWarning()
    {
        OnPropertyChanged(nameof(ResourceWarning));
        OnPropertyChanged(nameof(HasResourceWarning));
    }

    private void RaiseValidationChanged(string? propertyName)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    private List<string> GetValidationErrors(string? propertyName)
    {
        var errors = new List<string>();
        if (propertyName is null or nameof(ImageIdText))
        {
            if (!ImageId.TryParse(ImageIdText, out _))
            {
                errors.Add("환경 이름은 영문 소문자, 숫자, 내부 하이픈만 사용할 수 있습니다.");
            }
        }

        AddPathError(nameof(WindowsIsoPath), WindowsIsoPath, "Windows ISO");
        AddPathError(nameof(VirtioWinIsoPath), VirtioWinIsoPath, "virtio-win ISO");
        AddPathError(nameof(GuestBridgeExecutablePath), GuestBridgeExecutablePath, "GuestBridge");
        AddPathError(nameof(OvmfCodePath), OvmfCodePath, "OVMF 코드");
        AddPathError(nameof(OvmfVariablesTemplatePath), OvmfVariablesTemplatePath, "OVMF 변수 템플릿");
        if (propertyName is null or nameof(StagingDirectory))
        {
            if (!string.IsNullOrWhiteSpace(StagingDirectory) && !Path.IsPathFullyQualified(StagingDirectory))
            {
                errors.Add("스테이징 디렉터리는 절대 경로여야 합니다.");
            }
        }

        AddRangeError(nameof(DiskSizeGiB), DiskSizeGiB, 64, 1024, "디스크");
        AddRangeError(nameof(CpuCount), CpuCount, 2, 32, "가상 CPU");
        AddRangeError(nameof(MemoryMiB), MemoryMiB, 4096, 131072, "메모리");
        return errors;

        void AddPathError(string name, string value, string label)
        {
            if (propertyName is not null && !string.Equals(propertyName, name, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                errors.Add($"{label}의 절대 경로가 필요합니다.");
            }
        }

        void AddRangeError(string name, int value, int minimum, int maximum, string label)
        {
            if (propertyName is not null && !string.Equals(propertyName, name, StringComparison.Ordinal))
            {
                return;
            }

            if (value < minimum || value > maximum)
            {
                errors.Add($"{label} 값은 {minimum}~{maximum} 범위여야 합니다.");
            }
        }
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
        WindowsImageBuildPhase.InstallerRunning => "Windows를 설치하고 연결 구성 요소를 준비하고 있습니다. 열린 Windows 창을 닫지 마세요.",
        WindowsImageBuildPhase.ReadyToVerify => "설치가 끝났습니다. 미디어 없는 검증 부팅을 준비하고 있습니다…",
        WindowsImageBuildPhase.VerificationRunning => "Windows 환경을 확인하고 있습니다…",
        WindowsImageBuildPhase.ReadyToFinalize => "확인을 마쳤습니다. Windows 환경을 마무리하고 있습니다…",
        _ => "이미지 생성을 준비하고 있습니다…",
    };

    private sealed class ProgressLifetime
    {
        private int _active = 1;

        public bool IsActive => Volatile.Read(ref _active) == 1;

        public void Stop() => Interlocked.Exchange(ref _active, 0);
    }
}
