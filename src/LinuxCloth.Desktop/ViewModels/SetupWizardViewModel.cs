using System.Collections.ObjectModel;
using System.Diagnostics;
using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Setup;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class SetupWizardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HostCapacitySnapshot _hostCapacity;
    private readonly IPackageInstaller _packageInstaller;
    private readonly IDesktopSetupService _runtime;
    private readonly ISetupRunStore _runStore;
    private readonly ISetupStateStore _stateStore;
    private readonly SetupOrchestrator _orchestrator;
    private CancellationTokenSource? _mediaCancellation;
    private TaskCompletionSource? _mediaFinished;
    private CancellationTokenSource? _operationCancellation;
    private Task? _operationTask;
    private Task? _viewerTask;
    private SetupBlocker? _blocker;
    private string? _currentBuildStagingDirectory;
    private bool _hasLiveBuildDisplay;
    private int _cpuCount = 4;
    private int _diskSizeGiB = 96;
    private string? _errorMessage;
    private bool _hostInspected;
    private string _hostStatus = "자동 확인을 시작합니다.";
    private string _imageIdText = "windows-11";
    private bool _isBusy;
    private bool _isDisposed;
    private bool _isLicenseConfirmed;
    private bool _isRunning;
    private bool _isViewerOpen;
    private int _memoryMiB = 6144;
    private bool _rememberMediaPaths;
    private WindowsInstallationImage? _selectedWindowsEdition;
    private string _statusText = "Windows 설치 파일과 장치 드라이버를 선택하세요.";
    private SetupState _state = SetupState.Default;
    private string _virtioMediaPath = string.Empty;
    private SetupFileFingerprint? _virtioFingerprint;
    private string _virtioMediaStatus = "시작하면 검증된 Windows 장치 드라이버를 자동으로 준비합니다.";
    private string _windowsMediaPath = string.Empty;
    private SetupFileFingerprint? _windowsFingerprint;
    private string _windowsMediaStatus = "Windows 11 설치 파일을 선택하세요.";

    public SetupWizardViewModel(
        IDesktopSetupService runtime,
        FirstRunSnapshot firstRun,
        ISetupStateStore stateStore,
        ISetupRunStore runStore,
        DistributionInfoReader distributionReader,
        PackagePlanResolver packagePlanResolver,
        IPackageInstaller packageInstaller,
        HostCapacitySnapshot hostCapacity)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(firstRun);
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        ArgumentNullException.ThrowIfNull(distributionReader);
        ArgumentNullException.ThrowIfNull(packagePlanResolver);
        _packageInstaller = packageInstaller ?? throw new ArgumentNullException(nameof(packageInstaller));
        _hostCapacity = hostCapacity ?? throw new ArgumentNullException(nameof(hostCapacity));

        foreach (var title in new[]
                 {
                     "시스템 확인",
                     "필수 구성 요소",
                     "설치 파일 확인",
                     "Windows 설치",
                     "환경 확인",
                     "마무리",
                 })
        {
            Phases.Add(new SetupFlowPhaseItemViewModel(title));
        }

        var imageProgress = new Progress<DesktopImageBuildProgress>(ApplyImageBuildProgress);
        _orchestrator = new SetupOrchestrator(
            _runStore,
            DesktopSetupOperationFactory.Create(
                _runtime,
                distributionReader,
                packagePlanResolver,
                packageInstaller,
                imageProgress));
        PrepareCommand = new AsyncCommand(PrepareAsync, () => CanPrepare, ShowError);
        RetryCommand = new AsyncCommand(RetryAsync, () => CanRetry, ShowError);
        CancelCommand = new AsyncCommand(CancelAsync, () => IsRunning, ShowError);
        ViewInstallerCommand = new AsyncCommand(ViewInstallerAsync, () => CanViewInstaller, ShowError);
        ReinspectCommand = new AsyncCommand(RefreshHostAsync, () => !HasActiveOperation, ShowError);
        LaterCommand = new AsyncCommand(RequestLaterAsync, () => !HasActiveOperation, ShowError);
    }

    public event EventHandler? Completed;

    public event EventHandler? LaterRequested;

    public static Uri WindowsDownloadUri { get; } = new("https://www.microsoft.com/software-download/windows11");

    public ObservableCollection<WindowsInstallationImage> WindowsEditions { get; } = [];

    public ObservableCollection<SetupFlowPhaseItemViewModel> Phases { get; } = [];

    public AsyncCommand PrepareCommand { get; }

    public AsyncCommand RetryCommand { get; }

    public AsyncCommand CancelCommand { get; }

    public AsyncCommand ViewInstallerCommand { get; }

    public AsyncCommand ReinspectCommand { get; }

    public AsyncCommand LaterCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseState();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseState();
            }
        }
    }

    public bool IsBlocked => _blocker is not null;

    public bool IsReady => !IsRunning && !IsBlocked;

    public bool HasActiveOperation => IsBusy || IsRunning;

    public bool CanPrepare =>
        IsReady &&
        !IsBusy &&
        _hostInspected &&
        _windowsFingerprint is not null &&
        SelectedWindowsEdition is not null &&
        IsLicenseConfirmed &&
        ImageId.TryParse(ImageIdText, out _) &&
        DiskSizeGiB is >= 64 and <= 1024 &&
        CpuCount is >= 2 and <= 32 &&
        MemoryMiB is >= 4096 and <= 131072;

    public bool CanRetry => IsBlocked && !HasActiveOperation;

    public bool IsViewerOpen
    {
        get => _isViewerOpen;
        private set
        {
            if (SetProperty(ref _isViewerOpen, value))
            {
                OnPropertyChanged(nameof(CanViewInstaller));
                ViewInstallerCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanViewInstaller =>
        IsRunning &&
        !IsViewerOpen &&
        _hasLiveBuildDisplay &&
        !string.IsNullOrWhiteSpace(_currentBuildStagingDirectory);

    public string HostStatus
    {
        get => _hostStatus;
        private set => SetProperty(ref _hostStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
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

    public string BlockerTitle => _blocker?.Title ?? string.Empty;

    public string BlockerDescription => _blocker?.Description ?? string.Empty;

    public string BlockerActionLabel => _blocker?.ActionLabel ?? "다시 시도";

    public string BlockerCode => _blocker?.Code ?? string.Empty;

    public string TechnicalDetail => _blocker?.TechnicalDetail ?? string.Empty;

    public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);

    public bool ShowManualCommand => string.Equals(
        _blocker?.Code,
        "SETUP-PKG-MANUAL",
        StringComparison.Ordinal);

    public string ManualInstallCommand => ShowManualCommand ? TechnicalDetail : string.Empty;

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

    public string WindowsMediaName => MediaName(WindowsMediaPath, "선택되지 않음");

    public string WindowsMediaStatus
    {
        get => _windowsMediaStatus;
        private set => SetProperty(ref _windowsMediaStatus, value);
    }

    public string WindowsMediaHash => _windowsFingerprint?.Sha256 ?? string.Empty;

    public bool IsWindowsMediaValid => _windowsFingerprint is not null;

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

    public string VirtioMediaName => MediaName(VirtioMediaPath, "자동 준비");

    public string VirtioMediaStatus
    {
        get => _virtioMediaStatus;
        private set => SetProperty(ref _virtioMediaStatus, value);
    }

    public string VirtioMediaHash => _virtioFingerprint?.Sha256 ?? string.Empty;

    public bool IsVirtioMediaValid => _virtioFingerprint is not null;

    public WindowsInstallationImage? SelectedWindowsEdition
    {
        get => _selectedWindowsEdition;
        set
        {
            if (SetProperty(ref _selectedWindowsEdition, value))
            {
                RaiseState();
            }
        }
    }

    public bool HasMultipleWindowsEditions => WindowsEditions.Count > 1;

    public bool IsLicenseConfirmed
    {
        get => _isLicenseConfirmed;
        set
        {
            if (SetProperty(ref _isLicenseConfirmed, value))
            {
                RaiseState();
            }
        }
    }

    public bool RememberMediaPaths
    {
        get => _rememberMediaPaths;
        set => SetProperty(ref _rememberMediaPaths, value);
    }

    public string ImageIdText
    {
        get => _imageIdText;
        set
        {
            if (SetProperty(ref _imageIdText, value?.Trim() ?? string.Empty))
            {
                RaiseState();
            }
        }
    }

    public int DiskSizeGiB
    {
        get => _diskSizeGiB;
        set
        {
            if (SetProperty(ref _diskSizeGiB, value))
            {
                OnPropertyChanged(nameof(ResourceWarning));
                OnPropertyChanged(nameof(HasResourceWarning));
                RaiseState();
            }
        }
    }

    public int CpuCount
    {
        get => _cpuCount;
        set
        {
            if (SetProperty(ref _cpuCount, value))
            {
                OnPropertyChanged(nameof(ResourceWarning));
                OnPropertyChanged(nameof(HasResourceWarning));
                RaiseState();
            }
        }
    }

    public int MemoryMiB
    {
        get => _memoryMiB;
        set
        {
            if (SetProperty(ref _memoryMiB, value))
            {
                OnPropertyChanged(nameof(ResourceWarning));
                OnPropertyChanged(nameof(HasResourceWarning));
                RaiseState();
            }
        }
    }

    public string ResourceWarning
    {
        get
        {
            var warnings = new List<string>();
            if (_hostCapacity.LogicalProcessorCount > 0 && CpuCount > _hostCapacity.LogicalProcessorCount)
            {
                warnings.Add($"가상 CPU가 이 컴퓨터의 논리 CPU {_hostCapacity.LogicalProcessorCount}개보다 많습니다.");
            }

            if (_hostCapacity.AvailableMemoryBytes > 0 &&
                (long)MemoryMiB * 1024 * 1024 > _hostCapacity.AvailableMemoryBytes * 3 / 4)
            {
                warnings.Add("메모리 설정이 현재 사용 가능한 메모리의 75%를 초과합니다.");
            }

            return string.Join(' ', warnings);
        }
    }

    public bool HasResourceWarning => ResourceWarning.Length > 0;

    public async Task InitializeAsync()
    {
        if (_isDisposed || _hostInspected)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _state = await _stateStore.LoadAsync(_shutdown.Token).ConfigureAwait(true);
            RememberMediaPaths = _state.RememberMediaPaths;
            await RefreshHostCoreAsync().ConfigureAwait(true);
            var run = await _runStore.LoadAsync(_shutdown.Token).ConfigureAwait(true);
            if (run?.IsActive == true)
            {
                RestoreInputs(run.Inputs);
                await RunOrchestratorAsync(resume: true).ConfigureAwait(true);
                return;
            }

            if (RememberMediaPaths && _state.WindowsIsoPath is not null)
            {
                await ValidateWindowsMediaAsync(_state.WindowsIsoPath).ConfigureAwait(true);
            }

            if (RememberMediaPaths && _state.VirtioIsoPath is not null)
            {
                await ValidateVirtioMediaAsync(_state.VirtioIsoPath).ConfigureAwait(true);
            }

            if (_virtioFingerprint is null)
            {
                await LoadCachedVirtioMediaAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
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
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _mediaCancellation = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaFinished = finished;
        IsBusy = true;
        ErrorMessage = null;
        WindowsMediaPath = Path.GetFullPath(path);
        WindowsEditions.Clear();
        SelectedWindowsEdition = null;
        _windowsFingerprint = null;
        WindowsMediaStatus = "설치 가능한 Windows 버전과 파일 무결성을 확인하고 있습니다…";
        RaiseMediaState();
        try
        {
            var analysis = await _runtime.AnalyzeWindowsMediaAsync(
                    WindowsMediaPath,
                    cancellation.Token)
                .ConfigureAwait(true);
            _windowsFingerprint = ToSetupFingerprint(analysis.Fingerprint);
            foreach (var image in analysis.Catalog.SupportedImages)
            {
                WindowsEditions.Add(image);
            }

            OnPropertyChanged(nameof(HasMultipleWindowsEditions));
            SelectedWindowsEdition = analysis.Catalog.SuggestedImageIndex is int suggested
                ? WindowsEditions.Single(image => image.Index == suggested)
                : null;
            WindowsMediaStatus = SelectedWindowsEdition is null
                ? "파일 확인 완료 · 설치할 Windows 버전을 선택하세요."
                : $"파일 확인 완료 · {SelectedWindowsEdition.DisplayName}";
            await SavePreferencesAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            WindowsMediaStatus = "파일 확인을 중단했습니다.";
        }
        catch (Exception exception)
        {
            Trace.TraceError("Windows media analysis failed: {0}", exception);
            WindowsMediaStatus = "Windows 11 설치 파일을 확인하지 못했습니다. 다른 파일을 선택하세요.";
            ErrorMessage = WindowsMediaStatus;
        }
        finally
        {
            if (ReferenceEquals(_mediaCancellation, cancellation))
            {
                _mediaCancellation.Dispose();
                _mediaCancellation = null;
                _mediaFinished = null;
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
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _mediaCancellation = cancellation;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaFinished = finished;
        IsBusy = true;
        ErrorMessage = null;
        VirtioMediaPath = Path.GetFullPath(path);
        _virtioFingerprint = null;
        VirtioMediaStatus = "Windows 장치 드라이버를 확인하고 있습니다…";
        RaiseMediaState();
        try
        {
            var fingerprint = await _runtime.ValidateVirtioMediaAsync(
                    VirtioMediaPath,
                    cancellation.Token)
                .ConfigureAwait(true);
            _virtioFingerprint = ToSetupFingerprint(fingerprint);
            VirtioMediaStatus = "파일 확인 완료 · Windows 11 드라이버 사용 가능";
            await SavePreferencesAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            VirtioMediaStatus = "파일 확인을 중단했습니다.";
        }
        catch (Exception exception)
        {
            Trace.TraceError("Windows driver media validation failed: {0}", exception);
            VirtioMediaStatus = "Windows 장치 드라이버 파일을 확인하지 못했습니다. 다른 파일을 선택하세요.";
            ErrorMessage = VirtioMediaStatus;
        }
        finally
        {
            if (ReferenceEquals(_mediaCancellation, cancellation))
            {
                _mediaCancellation.Dispose();
                _mediaCancellation = null;
                _mediaFinished = null;
                IsBusy = false;
            }

            finished.TrySetResult();
            RaiseMediaState();
        }
    }

    public void ReportExternalActionError(Exception exception) => ShowError(exception);

    public async Task CancelAndWaitAsync()
    {
        _operationCancellation?.Cancel();
        if (_operationTask is not null)
        {
            await _operationTask.ConfigureAwait(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _shutdown.Cancel();
        await CancelMediaValidationAndWaitAsync().ConfigureAwait(true);
        await CancelAndWaitAsync().ConfigureAwait(true);
        if (_viewerTask is not null)
        {
            try
            {
                await _viewerTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        await SavePreferencesAsync(CancellationToken.None).ConfigureAwait(true);
        if (_packageInstaller is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(true);
        }

        _orchestrator.Dispose();
        _operationCancellation?.Dispose();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task PrepareAsync()
    {
        if (!CanPrepare)
        {
            return;
        }

        await SavePreferencesAsync().ConfigureAwait(true);
        await _runStore.ClearAsync(_shutdown.Token).ConfigureAwait(true);
        await RunOrchestratorAsync(resume: false).ConfigureAwait(true);
    }

    public async Task RetryAsync()
    {
        if (!CanRetry)
        {
            return;
        }

        var run = await _runStore.LoadAsync(_shutdown.Token).ConfigureAwait(true);
        if (run is null)
        {
            _blocker = null;
            RaiseBlockerState();
            return;
        }

        var updated = run with
        {
            Inputs = CreateInputs(run.Inputs),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _runStore.SaveAsync(updated, _shutdown.Token).ConfigureAwait(true);
        await RunOrchestratorAsync(resume: true).ConfigureAwait(true);
    }

    public async Task ViewInstallerAsync()
    {
        if (!CanViewInstaller)
        {
            return;
        }

        var staging = _currentBuildStagingDirectory!;
        IsViewerOpen = true;
        var viewerTask = _runtime.ViewImageBuildAsync(
            ImageId.Parse(ImageIdText),
            staging,
            _shutdown.Token);
        _viewerTask = viewerTask;
        try
        {
            await viewerTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_viewerTask, viewerTask))
            {
                _viewerTask = null;
            }

            IsViewerOpen = false;
        }
    }

    private Task CancelAsync() => CancelAndWaitAsync();

    private async Task RefreshHostAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await RefreshHostCoreAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshHostCoreAsync()
    {
        HostStatus = "이 컴퓨터를 확인하고 있습니다…";
        var doctor = await _runtime.InspectHostAsync(_shutdown.Token).ConfigureAwait(true);
        _hostInspected = true;
        HostStatus = DesktopSetupOperationFactory.CanBuildImage(doctor)
            ? "준비됨"
            : "필요한 구성 요소를 시작할 때 자동으로 준비합니다.";
        RaiseState();
    }

    private async Task RunOrchestratorAsync(bool resume)
    {
        if (IsRunning)
        {
            return;
        }

        _blocker = null;
        ErrorMessage = null;
        IsRunning = true;
        ApplyFlowProgress(0, "설치 준비를 시작합니다.");
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var cancellation = _operationCancellation;
        var progress = new Progress<SetupProgress>(item =>
        {
            StatusText = item.Status;
            ApplyPhase(item.Phase);
        });
        _operationTask = ExecuteOrchestratorAsync(resume, progress, cancellation.Token);
        try
        {
            await _operationTask.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, cancellation))
            {
                _operationCancellation.Dispose();
                _operationCancellation = null;
                _operationTask = null;
                IsRunning = false;
            }
        }
    }

    private async Task ExecuteOrchestratorAsync(
        bool resume,
        IProgress<SetupProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var run = resume
                ? await _orchestrator.ResumeAsync(progress, cancellationToken).ConfigureAwait(true)
                : await _orchestrator.StartAsync(CreateInputs(), progress, cancellationToken)
                    .ConfigureAwait(true);
            if (run.Phase == SetupPhase.Blocked)
            {
                _blocker = run.Blocker;
                StatusText = run.Blocker?.Description ?? "사용자 확인이 필요합니다.";
                RaiseBlockerState();
                return;
            }

            if (run.Phase == SetupPhase.Completed)
            {
                ApplyFlowProgress(Phases.Count, "Windows 환경 준비를 완료했습니다.");
                StatusText = "Windows 환경 준비를 완료했습니다.";
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "현재 작업을 안전하게 중단했습니다. 다음 실행에서 계속할 수 있습니다.";
        }
        catch (Exception exception)
        {
            ShowError(exception);
            StatusText = "설치 준비를 마치지 못했습니다. 보존된 작업에서 다시 시도할 수 있습니다.";
            var run = await _runStore.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            _blocker = new SetupBlocker(
                run?.Phase ?? SetupPhase.Discovering,
                SetupFailureKind.Retryable,
                "SETUP-UNEXPECTED",
                "Windows 환경 준비를 마치지 못했습니다",
                "현재 작업을 보존했습니다. 다시 시도하세요.",
                "다시 시도",
                exception.Message);
            RaiseBlockerState();
        }
    }

    private async Task RequestLaterAsync()
    {
        await SavePreferencesAsync().ConfigureAwait(true);
        LaterRequested?.Invoke(this, EventArgs.Empty);
    }

    private SetupInputSnapshot CreateInputs(SetupInputSnapshot? existing = null)
    {
        var edition = SelectedWindowsEdition;
        return new SetupInputSnapshot(
            EmptyToNull(WindowsMediaPath) ?? existing?.WindowsIsoPath,
            _windowsFingerprint ?? existing?.WindowsIsoFingerprint,
            EmptyToNull(VirtioMediaPath) ?? existing?.VirtioIsoPath,
            _virtioFingerprint ?? existing?.VirtioIsoFingerprint,
            edition?.Index ?? existing?.WindowsImageIndex,
            edition?.EditionId ?? existing?.WindowsEditionId,
            edition?.DisplayName ?? existing?.WindowsEdition,
            existing?.PackagePlanDigest,
            ImageId.Parse(ImageIdText),
            DiskSizeGiB,
            CpuCount,
            MemoryMiB,
            IsLicenseConfirmed);
    }

    private void RestoreInputs(SetupInputSnapshot inputs)
    {
        WindowsMediaPath = inputs.WindowsIsoPath ?? string.Empty;
        VirtioMediaPath = inputs.VirtioIsoPath ?? string.Empty;
        _windowsFingerprint = inputs.WindowsIsoFingerprint;
        _virtioFingerprint = inputs.VirtioIsoFingerprint;
        WindowsEditions.Clear();
        if (inputs.WindowsImageIndex is int index &&
            inputs.WindowsEditionId is { Length: > 0 } editionId &&
            inputs.WindowsEdition is { Length: > 0 } displayName)
        {
            var image = new WindowsInstallationImage(index, displayName, editionId, "amd64", 22000);
            WindowsEditions.Add(image);
            SelectedWindowsEdition = image;
        }

        ImageIdText = inputs.ImageId.Value;
        DiskSizeGiB = inputs.DiskSizeGiB;
        CpuCount = inputs.CpuCount;
        MemoryMiB = inputs.MemoryMiB;
        IsLicenseConfirmed = inputs.LicenseConfirmed;
        WindowsMediaStatus = _windowsFingerprint is null ? "파일을 다시 선택하세요." : "이전 작업의 설치 파일";
        VirtioMediaStatus = _virtioFingerprint is null
            ? "필요할 때 검증된 Windows 장치 드라이버를 자동으로 준비합니다."
            : "이전 작업의 장치 드라이버";
        RaiseMediaState();
    }

    private async Task LoadCachedVirtioMediaAsync()
    {
        VirtioMediaStatus = "캐시된 Windows 장치 드라이버를 확인하고 있습니다…";
        var cached = await _runtime.FindCachedVirtioMediaAsync(_shutdown.Token).ConfigureAwait(true);
        if (cached is null)
        {
            VirtioMediaStatus = "시작하면 검증된 Windows 장치 드라이버를 자동으로 준비합니다.";
            return;
        }

        VirtioMediaPath = cached.Path;
        _virtioFingerprint = ToSetupFingerprint(cached);
        VirtioMediaStatus = "자동 준비됨 · 고정된 Windows 11 드라이버";
        RaiseMediaState();
    }

    private async Task SavePreferencesAsync(CancellationToken? cancellationToken = null)
    {
        _state = _state with
        {
            LastStep = SetupStep.HostInspection,
            RememberMediaPaths = RememberMediaPaths,
            WindowsIsoPath = RememberMediaPaths ? EmptyToNull(WindowsMediaPath) : null,
            VirtioIsoPath = RememberMediaPaths ? EmptyToNull(VirtioMediaPath) : null,
        };
        await _stateStore.SaveAsync(
                _state,
                cancellationToken ?? _shutdown.Token)
            .ConfigureAwait(true);
    }

    private void ApplyImageBuildProgress(DesktopImageBuildProgress progress)
    {
        _currentBuildStagingDirectory = progress.StagingDirectory ?? _currentBuildStagingDirectory;
        _hasLiveBuildDisplay = progress.Phase is WindowsImageBuildPhase.InstallerRunning or
            WindowsImageBuildPhase.VerificationRunning;
        OnPropertyChanged(nameof(CanViewInstaller));
        ViewInstallerCommand.RaiseCanExecuteChanged();
        var phaseIndex = progress.Phase switch
        {
            WindowsImageBuildPhase.Preparing or WindowsImageBuildPhase.Prepared => 2,
            WindowsImageBuildPhase.InstallerRunning => 3,
            WindowsImageBuildPhase.ReadyToVerify or WindowsImageBuildPhase.VerificationRunning => 4,
            WindowsImageBuildPhase.ReadyToFinalize => 5,
            _ => 3,
        };
        ApplyFlowProgress(phaseIndex, StatusText);
    }

    private void ApplyPhase(SetupPhase phase)
    {
        var index = phase switch
        {
            SetupPhase.Discovering or SetupPhase.AwaitingInputs => 0,
            SetupPhase.InstallingDependencies => 1,
            SetupPhase.ValidatingMedia or SetupPhase.PlanningWindows => 2,
            SetupPhase.BuildingImage => 3,
            SetupPhase.Finalizing => 5,
            SetupPhase.Completed => Phases.Count,
            _ => 0,
        };
        ApplyFlowProgress(index, StatusText);
    }

    private void ApplyFlowProgress(int currentIndex, string status)
    {
        StatusText = status;
        for (var index = 0; index < Phases.Count; index++)
        {
            Phases[index].Update(index < currentIndex, index == currentIndex);
        }
    }

    private void RaiseBlockerState()
    {
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(BlockerTitle));
        OnPropertyChanged(nameof(BlockerDescription));
        OnPropertyChanged(nameof(BlockerActionLabel));
        OnPropertyChanged(nameof(BlockerCode));
        OnPropertyChanged(nameof(TechnicalDetail));
        OnPropertyChanged(nameof(HasTechnicalDetail));
        OnPropertyChanged(nameof(ShowManualCommand));
        OnPropertyChanged(nameof(ManualInstallCommand));
        RaiseState();
    }

    private void RaiseMediaState()
    {
        OnPropertyChanged(nameof(IsWindowsMediaValid));
        OnPropertyChanged(nameof(WindowsMediaHash));
        OnPropertyChanged(nameof(IsVirtioMediaValid));
        OnPropertyChanged(nameof(VirtioMediaHash));
        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(HasActiveOperation));
        OnPropertyChanged(nameof(CanPrepare));
        OnPropertyChanged(nameof(CanRetry));
        PrepareCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ViewInstallerCommand.RaiseCanExecuteChanged();
        ReinspectCommand.RaiseCanExecuteChanged();
        LaterCommand.RaiseCanExecuteChanged();
    }

    private async Task CancelMediaValidationAndWaitAsync()
    {
        _mediaCancellation?.Cancel();
        if (_mediaFinished is not null)
        {
            await _mediaFinished.Task.ConfigureAwait(true);
        }
    }

    private void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Desktop setup operation failed: {0}", exception);
        ErrorMessage = "작업을 완료하지 못했습니다. 기술 세부정보를 확인한 뒤 다시 시도하세요.";
    }

    private static SetupFileFingerprint ToSetupFingerprint(ImageBuildFileFingerprint fingerprint) =>
        new(
            fingerprint.Length,
            new DateTimeOffset(fingerprint.LastWriteUtcTicks, TimeSpan.Zero),
            fingerprint.Sha256);

    private static string MediaName(string path, string fallback) =>
        string.IsNullOrWhiteSpace(path) ? fallback : Path.GetFileName(path);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class SetupFlowPhaseItemViewModel : ObservableObject
{
    private bool _isComplete;
    private bool _isCurrent;

    public SetupFlowPhaseItemViewModel(string title)
    {
        Title = title;
    }

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

    public string Marker => IsComplete ? "완료" : IsCurrent ? "진행 중" : "대기";

    public void Update(bool isComplete, bool isCurrent)
    {
        IsComplete = isComplete;
        IsCurrent = isCurrent;
        OnPropertyChanged(nameof(Marker));
    }
}
