using System.Collections.ObjectModel;
using System.Diagnostics;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class SetupWizardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DistributionInfoReader _distributionReader;
    private readonly IPackageInstaller _packageInstaller;
    private readonly PackagePlanResolver _packagePlanResolver;
    private readonly IDesktopSetupService _runtime;
    private readonly ISetupStateStore _stateStore;
    private readonly HostCapacitySnapshot _hostCapacity;
    private DesktopStartupSnapshot _startup;
    private SetupReadiness _readiness;
    private CancellationTokenSource? _mediaValidation;
    private TaskCompletionSource? _mediaValidationFinished;
    private SetupState _state = SetupState.Default;
    private PackageInstallPreview? _packagePreview;
    private PackagePlan? _packagePlan;
    private ImageBuildFileFingerprint? _virtioFingerprint;
    private ImageBuildFileFingerprint? _windowsFingerprint;
    private SetupStep _currentStep;
    private string _distributionLabel = "배포판 확인 대기 중";
    private string? _errorMessage;
    private bool _isBusy;
    private bool _disposed;
    private bool _isInitialized;
    private bool _isLicenseConfirmed;
    private bool _rememberMediaPaths;
    private string _packageStatus = "설치 계획을 준비하지 않았습니다.";
    private string _virtioMediaPath = string.Empty;
    private string _virtioMediaStatus = "Windows 장치 드라이버 파일을 선택하세요.";
    private string _windowsMediaPath = string.Empty;
    private string _windowsMediaStatus = "Windows 11 x64 ISO를 선택하세요.";

    public SetupWizardViewModel(
        IDesktopSetupService runtime,
        FirstRunSnapshot firstRun,
        ISetupStateStore stateStore,
        DistributionInfoReader distributionReader,
        PackagePlanResolver packagePlanResolver,
        IPackageInstaller packageInstaller,
        HostCapacitySnapshot hostCapacity)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(firstRun);
        _startup = firstRun.Startup;
        _readiness = firstRun.Readiness;
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _distributionReader = distributionReader ?? throw new ArgumentNullException(nameof(distributionReader));
        _packagePlanResolver = packagePlanResolver ?? throw new ArgumentNullException(nameof(packagePlanResolver));
        _packageInstaller = packageInstaller ?? throw new ArgumentNullException(nameof(packageInstaller));
        _hostCapacity = hostCapacity ?? throw new ArgumentNullException(nameof(hostCapacity));
        _currentStep = SetupStep.HostInspection;

        foreach (var (step, title) in StepDefinitions)
        {
            Steps.Add(new SetupStepItemViewModel(step, title));
        }

        Build = new ImageSetupViewModel(_runtime, OnImageRegisteredAsync, _shutdown.Token);
        Build.ApplyHostCapacity(_hostCapacity);
        BackCommand = new AsyncCommand(GoBackAsync, () => CanGoBack, ShowError);
        ContinueCommand = new AsyncCommand(ContinueAsync, () => CanContinue, ShowError);
        ReinspectCommand = new AsyncCommand(RefreshHostAsync, () => !IsBusy, ShowError);
        InstallPackagesCommand = new AsyncCommand(
            InstallPackagesAsync,
            () => CanInstallPackages,
            ShowError);
        LaterCommand = new AsyncCommand(SaveAndRequestLaterAsync, () => !IsBusy, ShowError);
        ApplySnapshot(_startup);
        UpdateStepState();
    }

    public event EventHandler? Completed;

    public event EventHandler? LaterRequested;

    public static Uri WindowsDownloadUri { get; } = new("https://www.microsoft.com/software-download/windows11");

    public static Uri VirtioDownloadUri { get; } = new("https://github.com/virtio-win/virtio-win-pkg-scripts/blob/master/README.md");

    private static (SetupStep Step, string Title)[] StepDefinitions { get; } =
    [
        (SetupStep.HostInspection, "시스템 확인"),
        (SetupStep.Components, "필수 구성 요소"),
        (SetupStep.WindowsMedia, "Windows 설치 파일"),
        (SetupStep.VirtioMedia, "Windows 드라이버"),
        (SetupStep.ImageBuild, "Windows 환경 만들기"),
    ];

    public ObservableCollection<SetupStepItemViewModel> Steps { get; } = [];

    public ObservableCollection<DoctorCheckViewModel> HostChecks { get; } = [];

    public ObservableCollection<PackageChange> PackageChanges { get; } = [];

    public ImageSetupViewModel Build { get; }

    public AsyncCommand BackCommand { get; }

    public AsyncCommand ContinueCommand { get; }

    public AsyncCommand ReinspectCommand { get; }

    public AsyncCommand InstallPackagesCommand { get; }

    public AsyncCommand LaterCommand { get; }

    public SetupStep CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (SetProperty(ref _currentStep, value))
            {
                OnPropertyChanged(nameof(IsHostInspectionStep));
                OnPropertyChanged(nameof(IsComponentsStep));
                OnPropertyChanged(nameof(IsWindowsMediaStep));
                OnPropertyChanged(nameof(IsVirtioMediaStep));
                OnPropertyChanged(nameof(IsImageBuildStep));
                OnPropertyChanged(nameof(CurrentTitle));
                OnPropertyChanged(nameof(CurrentDescription));
                OnPropertyChanged(nameof(StepProgressText));
                UpdateStepState();
                RaiseNavigationState();
            }
        }
    }

    public bool IsHostInspectionStep => CurrentStep == SetupStep.HostInspection;

    public bool IsComponentsStep => CurrentStep == SetupStep.Components;

    public bool IsWindowsMediaStep => CurrentStep == SetupStep.WindowsMedia;

    public bool IsVirtioMediaStep => CurrentStep == SetupStep.VirtioMedia;

    public bool IsImageBuildStep => CurrentStep == SetupStep.ImageBuild;

    public string CurrentTitle => StepDefinitions.First(item => item.Step == CurrentStep).Title;

    public string CurrentDescription => CurrentStep switch
    {
        SetupStep.HostInspection => "이 컴퓨터에서 Windows 환경을 실행할 수 있는지 확인합니다.",
        SetupStep.Components => "실행에 필요한 구성 요소를 준비합니다.",
        SetupStep.WindowsMedia => "Windows 11 설치 파일을 선택합니다.",
        SetupStep.VirtioMedia => "디스크·네트워크 드라이버를 선택합니다.",
        SetupStep.ImageBuild => "선택한 파일로 Windows 환경을 만듭니다.",
        _ => string.Empty,
    };

    public string StepProgressText => $"{(int)CurrentStep + 1}/{StepDefinitions.Length} 단계";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseNavigationState();
            }
        }
    }

    public bool HasActiveOperation => IsBusy || Build.IsBuilding;

    public bool CanGoBack => !IsBusy && !Build.IsBuilding && CurrentStep > SetupStep.HostInspection;

    public bool CanContinue => !IsBusy && !Build.IsBuilding && CurrentStep switch
    {
        SetupStep.HostInspection => IsDoctorAvailable(QemuDoctorCheckCodes.Platform),
        SetupStep.Components => _readiness.CanBuildImage,
        SetupStep.WindowsMedia => _windowsFingerprint is not null && IsLicenseConfirmed,
        SetupStep.VirtioMedia => _virtioFingerprint is not null,
        SetupStep.ImageBuild => false,
        _ => false,
    };

    public string ContinueLabel => CurrentStep == SetupStep.VirtioMedia ? "검토하기" : "계속";

    public bool IsLicenseConfirmed
    {
        get => _isLicenseConfirmed;
        set
        {
            if (SetProperty(ref _isLicenseConfirmed, value))
            {
                RaiseNavigationState();
            }
        }
    }

    public bool RememberMediaPaths
    {
        get => _rememberMediaPaths;
        set => SetProperty(ref _rememberMediaPaths, value);
    }

    public string WindowsMediaPath
    {
        get => _windowsMediaPath;
        private set
        {
            if (SetProperty(ref _windowsMediaPath, value))
            {
                OnPropertyChanged(nameof(WindowsMediaName));
            }
        }
    }

    public string WindowsMediaName => MediaName(WindowsMediaPath);

    public string WindowsMediaStatus
    {
        get => _windowsMediaStatus;
        private set => SetProperty(ref _windowsMediaStatus, value);
    }

    public bool IsWindowsMediaValid => _windowsFingerprint is not null;

    public string WindowsMediaHash => _windowsFingerprint?.Sha256 ?? string.Empty;

    public string VirtioMediaPath
    {
        get => _virtioMediaPath;
        private set
        {
            if (SetProperty(ref _virtioMediaPath, value))
            {
                OnPropertyChanged(nameof(VirtioMediaName));
            }
        }
    }

    public string VirtioMediaName => MediaName(VirtioMediaPath);

    public string VirtioMediaStatus
    {
        get => _virtioMediaStatus;
        private set => SetProperty(ref _virtioMediaStatus, value);
    }

    public bool IsVirtioMediaValid => _virtioFingerprint is not null;

    public string VirtioMediaHash => _virtioFingerprint?.Sha256 ?? string.Empty;

    public string DistributionLabel
    {
        get => _distributionLabel;
        private set => SetProperty(ref _distributionLabel, value);
    }

    public string PackageStatus
    {
        get => _packageStatus;
        private set => SetProperty(ref _packageStatus, value);
    }

    public bool CanInstallPackages => !IsBusy && _packagePreview?.CanInstall == true;

    public bool ShowManualCommand => _packagePlan is not null && _packagePreview?.IsPackageKitAvailable == false;

    public string ManualInstallCommand => _packagePlan?.ManualInstallCommand ?? string.Empty;

    public string PackageRepositories => _packagePreview is null
        ? string.Empty
        : string.Join(", ", _packagePreview.Repositories);

    public string PackageDownloadSize => _packagePreview is null
        ? string.Empty
        : FormatBytes(_packagePreview.DownloadSize);

    public string UnresolvedPackages => _packagePreview is null
        ? string.Empty
        : string.Join(", ", _packagePreview.UnresolvedPackages);

    public bool HasUnresolvedPackages => _packagePreview?.UnresolvedPackages.Count > 0;

    public bool CanBuildImage => _readiness.CanBuildImage;

    public bool CanLaunchOnline => _readiness.CanLaunchOnline;

    public string GuestBridgeStatus => _readiness.IsGuestBridgeAvailable
        ? "준비됨"
        : "구성 요소를 찾지 못했습니다";

    public string FirmwareStatus => _readiness.HasCompatibleFirmware
        ? "준비됨"
        : "시작 구성 요소를 찾지 못했습니다";

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
        IsBusy = true;
        try
        {
            await Build.InitializeAsync().ConfigureAwait(true);
            _state = await _stateStore.LoadAsync(_shutdown.Token).ConfigureAwait(true);
            RememberMediaPaths = _state.RememberMediaPaths;
            WindowsMediaPath = _state.WindowsIsoPath ?? string.Empty;
            VirtioMediaPath = _state.VirtioIsoPath ?? string.Empty;
            var resume = _readiness.PreferredResumableBuild;
            if (resume is not null)
            {
                Build.ImageIdText = resume.ImageId.Value;
                Build.StagingDirectory = resume.StagingDirectory;
                CurrentStep = SetupStep.ImageBuild;
            }
            else
            {
                CurrentStep = _state.LastStep;
            }

            if (CurrentStep >= SetupStep.Components)
            {
                await PreparePackagePlanAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
            CurrentStep = SetupStep.HostInspection;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ValidateWindowsMediaAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await CancelMediaValidationAndWaitAsync().ConfigureAwait(true);
        _mediaValidation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var validation = _mediaValidation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaValidationFinished = finished;
        IsBusy = true;
        WindowsMediaPath = Path.GetFullPath(path);
        _windowsFingerprint = null;
        Build.WindowsIsoPath = string.Empty;
        ErrorMessage = null;
        WindowsMediaStatus = "x64 부팅 파일, Windows 설치 이미지와 SHA-256을 확인하고 있습니다…";
        RaiseMediaState();
        try
        {
            var fingerprint = await _runtime
                .ValidateWindowsMediaAsync(WindowsMediaPath, validation.Token)
                .ConfigureAwait(true);
            _windowsFingerprint = fingerprint;
            Build.WindowsIsoPath = fingerprint.Path;
            WindowsMediaStatus = $"사용 가능 · {FormatBytes((ulong)fingerprint.Length)} · SHA-256 계산 완료";
            await SaveStateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Windows media validation failed: {0}", exception);
            WindowsMediaStatus = MediaError(exception, windows: true);
            ErrorMessage = WindowsMediaStatus;
        }
        finally
        {
            if (ReferenceEquals(_mediaValidation, validation))
            {
                _mediaValidation.Dispose();
                _mediaValidation = null;
                _mediaValidationFinished = null;
                IsBusy = false;
            }

            finished.TrySetResult();
            RaiseMediaState();
        }
    }

    public async Task ValidateVirtioMediaAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await CancelMediaValidationAndWaitAsync().ConfigureAwait(true);
        _mediaValidation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var validation = _mediaValidation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaValidationFinished = finished;
        IsBusy = true;
        VirtioMediaPath = Path.GetFullPath(path);
        _virtioFingerprint = null;
        Build.VirtioWinIsoPath = string.Empty;
        ErrorMessage = null;
        VirtioMediaStatus = "Windows 장치 드라이버를 확인하고 있습니다…";
        RaiseMediaState();
        try
        {
            var fingerprint = await _runtime
                .ValidateVirtioMediaAsync(VirtioMediaPath, validation.Token)
                .ConfigureAwait(true);
            _virtioFingerprint = fingerprint;
            Build.VirtioWinIsoPath = fingerprint.Path;
            VirtioMediaStatus = $"사용 가능 · {FormatBytes((ulong)fingerprint.Length)} · SHA-256 계산 완료";
            await SaveStateAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError("Windows driver media validation failed: {0}", exception);
            VirtioMediaStatus = MediaError(exception, windows: false);
            ErrorMessage = VirtioMediaStatus;
        }
        finally
        {
            if (ReferenceEquals(_mediaValidation, validation))
            {
                _mediaValidation.Dispose();
                _mediaValidation = null;
                _mediaValidationFinished = null;
                IsBusy = false;
            }

            finished.TrySetResult();
            RaiseMediaState();
        }
    }

    public void ReportExternalActionError(Exception exception) => ShowError(exception);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        await CancelMediaValidationAndWaitAsync().ConfigureAwait(true);
        await Build.DisposeAsync().ConfigureAwait(true);
        if (_packageInstaller is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(true);
        }

        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task ContinueAsync()
    {
        if (!CanContinue)
        {
            return;
        }

        CurrentStep++;
        if (CurrentStep == SetupStep.Components && _packagePlan is null)
        {
            IsBusy = true;
            try
            {
                await PreparePackagePlanAsync().ConfigureAwait(true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        await SaveStateAsync().ConfigureAwait(true);
    }

    public async Task GoBackAsync()
    {
        if (!CanGoBack)
        {
            return;
        }

        CurrentStep--;
        await SaveStateAsync().ConfigureAwait(true);
    }

    private async Task PreparePackagePlanAsync()
    {
        ErrorMessage = null;
        PackageStatus = "필요한 구성 요소를 확인하고 있습니다…";
        var distribution = await _distributionReader.ReadAsync(_shutdown.Token).ConfigureAwait(true);
        DistributionLabel = $"{distribution.Name ?? distribution.Id} {distribution.VersionId}".Trim();
        if (distribution.Family == DistributionFamily.Unsupported)
        {
            PackageStatus = "이 Linux 배포판에서는 자동 설치를 지원하지 않습니다. 세부정보의 누락 항목을 배포판 문서에 따라 설치하세요.";
            return;
        }

        _packagePlan = await _packagePlanResolver.ResolveAsync(distribution, _shutdown.Token)
            .ConfigureAwait(true);
        _packagePreview = await _packageInstaller.ResolveAsync(_packagePlan, _shutdown.Token)
            .ConfigureAwait(true);
        PackageChanges.Clear();
        foreach (var change in _packagePreview.Changes)
        {
            PackageChanges.Add(change);
        }

        PackageStatus = _packagePreview.IsAlreadySatisfied
            ? "필수 구성 요소가 이미 설치되어 있습니다."
            : !_packagePreview.IsPackageKitAvailable
                ? "자동 설치를 사용할 수 없습니다. 아래 명령을 터미널에서 직접 실행한 뒤 다시 확인하세요."
                : _packagePreview.UnresolvedPackages.Count > 0
                    ? "공식 저장소에서 해결하지 못한 패키지가 있습니다. 저장소 설정을 확인하세요."
                    : "필요한 구성 요소를 설치할 준비가 되었습니다. 시스템 인증이 요청될 수 있습니다.";
        RaisePackageState();
    }

    private async Task InstallPackagesAsync()
    {
        if (_packagePreview?.CanInstall != true)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var progress = new Progress<PackageInstallProgress>(value =>
                PackageStatus = value.PackageName is null
                    ? value.Status
                    : $"{value.Status} · {value.PackageName}");
            var result = await _packageInstaller
                .InstallAsync(_packagePreview, progress, _shutdown.Token)
                .ConfigureAwait(true);
            PackageStatus = result.Message;
            await RefreshHostCoreAsync().ConfigureAwait(true);
            await PreparePackagePlanAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshHostAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await RefreshHostCoreAsync().ConfigureAwait(true);
            if (_packagePlan is not null)
            {
                await PreparePackagePlanAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshHostCoreAsync()
    {
        var doctor = await _runtime.InspectHostAsync(_shutdown.Token).ConfigureAwait(true);
        var defaults = await _runtime.GetImageBuildDefaultsAsync(_shutdown.Token).ConfigureAwait(true);
        _startup = _startup with { Doctor = doctor, ImageBuildDefaults = defaults };
        ApplySnapshot(_startup);
        Build.ApplyDefaults(defaults);
    }

    private void ApplySnapshot(DesktopStartupSnapshot startup)
    {
        _readiness = SetupReadinessEvaluator.Evaluate(startup);
        HostChecks.Clear();
        foreach (var check in startup.Doctor.Report.Checks)
        {
            HostChecks.Add(new DoctorCheckViewModel(
                DoctorLabel(check.Name),
                check.IsAvailable,
                check.IsRequired,
                check.Detail));
        }

        HostChecks.Add(new DoctorCheckViewModel(
            "호스트 메모리",
            _hostCapacity.HasRecommendedMemory,
            IsRequired: false,
            _hostCapacity.AvailableMemoryBytes <= 0
                ? "사용 가능한 메모리를 확인하지 못했습니다."
                : $"사용 가능 {FormatBytes((ulong)_hostCapacity.AvailableMemoryBytes)} · 기본값 권장 6 GiB"));
        HostChecks.Add(new DoctorCheckViewModel(
            "호스트 디스크",
            _hostCapacity.HasMinimumDiskSpace,
            IsRequired: false,
            _hostCapacity.AvailableDiskBytes <= 0
                ? "사용 가능한 디스크 공간을 확인하지 못했습니다."
                : $"사용 가능 {FormatBytes((ulong)_hostCapacity.AvailableDiskBytes)} · 최소 권장 64 GiB"));

        OnPropertyChanged(nameof(CanBuildImage));
        OnPropertyChanged(nameof(CanLaunchOnline));
        OnPropertyChanged(nameof(GuestBridgeStatus));
        OnPropertyChanged(nameof(FirmwareStatus));
        RaiseNavigationState();
    }

    private async Task SaveAndRequestLaterAsync()
    {
        await SaveStateAsync().ConfigureAwait(true);
        LaterRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnImageRegisteredAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _state = SetupState.Default;
        await _stateStore.SaveAsync(_state, CancellationToken.None).ConfigureAwait(true);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private Task SaveStateAsync()
    {
        _state = new SetupState(
            SetupState.CurrentSchemaVersion,
            SetupState.CurrentWizardVersion,
            CurrentStep,
            SetupState.CurrentNoticeVersion,
            RememberMediaPaths,
            RememberMediaPaths ? EmptyToNull(WindowsMediaPath) : null,
            RememberMediaPaths ? EmptyToNull(VirtioMediaPath) : null,
            EmptyToNull(Build.StagingDirectory));
        return _stateStore.SaveAsync(_state, _shutdown.Token);
    }

    private void UpdateStepState()
    {
        foreach (var step in Steps)
        {
            step.Update(step.Step < CurrentStep, step.Step == CurrentStep);
        }
    }

    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(ContinueLabel));
        BackCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
        ReinspectCommand.RaiseCanExecuteChanged();
        InstallPackagesCommand.RaiseCanExecuteChanged();
        LaterCommand.RaiseCanExecuteChanged();
    }

    private void RaisePackageState()
    {
        OnPropertyChanged(nameof(CanInstallPackages));
        OnPropertyChanged(nameof(ShowManualCommand));
        OnPropertyChanged(nameof(ManualInstallCommand));
        OnPropertyChanged(nameof(PackageRepositories));
        OnPropertyChanged(nameof(PackageDownloadSize));
        OnPropertyChanged(nameof(UnresolvedPackages));
        OnPropertyChanged(nameof(HasUnresolvedPackages));
        RaiseNavigationState();
    }

    private void RaiseMediaState()
    {
        OnPropertyChanged(nameof(IsWindowsMediaValid));
        OnPropertyChanged(nameof(WindowsMediaHash));
        OnPropertyChanged(nameof(IsVirtioMediaValid));
        OnPropertyChanged(nameof(VirtioMediaHash));
        RaiseNavigationState();
    }

    private async Task CancelMediaValidationAndWaitAsync()
    {
        _mediaValidation?.Cancel();
        if (_mediaValidationFinished is not null)
        {
            await _mediaValidationFinished.Task.ConfigureAwait(true);
        }
    }

    private bool IsDoctorAvailable(string code) => _startup.Doctor.Report.Checks.Any(check =>
        string.Equals(check.Name, code, StringComparison.Ordinal) && check.IsAvailable);

    private void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Desktop setup operation failed: {0}", exception);
        ErrorMessage = exception switch
        {
            OperationCanceledException when _shutdown.IsCancellationRequested => "앱 종료로 작업이 취소되었습니다.",
            OperationCanceledException => "작업이 취소되었습니다.",
            UnauthorizedAccessException => "현재 사용자에게 이 작업을 수행할 권한이 없습니다.",
            _ => "설정 작업 중 오류가 발생했습니다. 다시 시도하거나 로그를 확인하세요.",
        };
    }

    private static string MediaError(Exception exception, bool windows) => exception switch
    {
        OperationCanceledException => "미디어 검증이 취소되었습니다.",
        FileNotFoundException => "선택한 파일을 찾지 못했습니다.",
        UnauthorizedAccessException => "현재 사용자에게 선택한 파일을 읽을 권한이 없습니다.",
        WindowsImageBuildException when windows && exception.Message.Contains("Windows x64", StringComparison.Ordinal) =>
            "Windows x64 부팅 파일을 찾지 못했습니다.",
        WindowsImageBuildException when windows && exception.Message.Contains("install.wim", StringComparison.Ordinal) =>
            "install.wim 또는 install.esd를 찾지 못했습니다.",
        WindowsImageBuildException when !windows && exception.Message.Contains("storage and network", StringComparison.Ordinal) =>
            "Windows 11용 디스크 또는 네트워크 드라이버를 찾지 못했습니다.",
        WindowsImageBuildException when exception.Message.Contains("size", StringComparison.Ordinal) =>
            windows ? "Windows 설치 파일이 허용된 32 GiB 크기 제한을 벗어났습니다." : "드라이버 파일이 허용된 8 GiB 크기 제한을 벗어났습니다.",
        _ => "선택한 파일을 확인하지 못했습니다. 다른 파일을 선택하거나 로그를 확인하세요.",
    };

    private static string DoctorLabel(string code) => code switch
    {
        QemuDoctorCheckCodes.Platform => "운영체제",
        QemuDoctorCheckCodes.Kvm => "가상화",
        QemuDoctorCheckCodes.QemuSystem => "Windows 실행",
        QemuDoctorCheckCodes.QemuImg => "Windows 디스크",
        QemuDoctorCheckCodes.Firmware => "Windows 시작",
        QemuDoctorCheckCodes.Swtpm => "보안 장치",
        QemuDoctorCheckCodes.Bubblewrap => "프로세스 격리",
        QemuDoctorCheckCodes.RemoteViewer => "Windows 화면",
        QemuDoctorCheckCodes.WimlibImagex => "Windows 설치 파일",
        QemuDoctorCheckCodes.Xorriso => "드라이버 파일",
        QemuDoctorCheckCodes.Passt => "인터넷 연결",
        QemuDoctorCheckCodes.RuntimeDirectory => "작업 공간",
        _ => code,
    };

    private static string MediaName(string path) => string.IsNullOrWhiteSpace(path)
        ? "선택되지 않음"
        : Path.GetFileName(path);

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class SetupStepItemViewModel : ObservableObject
{
    private bool _isComplete;
    private bool _isCurrent;

    public SetupStepItemViewModel(SetupStep step, string title)
    {
        Step = step;
        Title = title;
    }

    public SetupStep Step { get; }

    public int Number => (int)Step + 1;

    public string Title { get; }

    public bool IsComplete
    {
        get => _isComplete;
        private set => SetProperty(ref _isComplete, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    public string Marker => IsComplete ? "✓" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Update(bool isComplete, bool isCurrent)
    {
        IsComplete = isComplete;
        IsCurrent = isCurrent;
        OnPropertyChanged(nameof(Marker));
    }
}
