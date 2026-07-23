using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly List<ServiceCardViewModel> _allServices = [];
    private readonly DesktopRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _canHostLaunch;
    private string _doctorSummary = "확인 중";
    private string? _errorMessage;
    private bool _hasUnresolvedRecovery;
    private bool _isBusy;
    private bool _isInitialized;
    private IRunningLinuxClothSession? _runningSession;
    private string _searchText = string.Empty;
    private CategoryFilterViewModel? _selectedCategory;
    private ImageChoiceViewModel? _selectedImage;
    private ServiceCardViewModel? _selectedService;
    private string _sessionStatus = "서비스를 선택하세요.";

    public MainWindowViewModel(DesktopRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Categories.Add(new CategoryFilterViewModel("전체", null));
        SelectedCategory = Categories[0];
        RefreshDoctorCommand = new AsyncCommand(
            RefreshDoctorAsync,
            () => !IsBusy,
            ShowError);
        LaunchCommand = new AsyncCommand(
            LaunchSelectedAsync,
            () => CanLaunch,
            ShowError);
        StopCommand = new AsyncCommand(
            StopSessionAsync,
            () => IsSessionRunning,
            ShowError);
    }

    public event EventHandler? SetupRequested;

    public ObservableCollection<CategoryFilterViewModel> Categories { get; } = [];

    public ObservableCollection<ServiceCardViewModel> FilteredServices { get; } = [];

    public ObservableCollection<ImageChoiceViewModel> Images { get; } = [];

    public ObservableCollection<DoctorCheckViewModel> DoctorChecks { get; } = [];

    public AsyncCommand RefreshDoctorCommand { get; }

    public AsyncCommand LaunchCommand { get; }

    public AsyncCommand StopCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public CategoryFilterViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyFilter();
            }
        }
    }

    public ServiceCardViewModel? SelectedService
    {
        get => _selectedService;
        set
        {
            if (SetProperty(ref _selectedService, value))
            {
                OnPropertyChanged(nameof(HasSelectedService));
                RaiseCommandState();
            }
        }
    }

    public ImageChoiceViewModel? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (SetProperty(ref _selectedImage, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool HasSelectedService => SelectedService is not null;

    public bool HasImages => Images.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandState();
            }
        }
    }

    public bool IsSessionRunning => _runningSession is not null;

    public bool IsReady => _canHostLaunch && !_hasUnresolvedRecovery;

    public bool CanConfigureImages => !IsBusy && !IsSessionRunning;

    public bool CanLaunch =>
        _isInitialized &&
        _canHostLaunch &&
        !_hasUnresolvedRecovery &&
        !IsBusy &&
        !IsSessionRunning &&
        SelectedService is { IsBlocked: false } &&
        SelectedImage is not null;

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set => SetProperty(ref _doctorSummary, value);
    }

    public string SessionStatus
    {
        get => _sessionStatus;
        private set => SetProperty(ref _sessionStatus, value);
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

    public Task InitializeAsync(DesktopStartupSnapshot startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        if (_isInitialized)
        {
            return Task.CompletedTask;
        }

        IsBusy = true;
        ErrorMessage = null;
        SessionStatus = "준비 중…";
        try
        {
            LoadCatalog(startup.Catalog.Services);
            LoadImages(startup.Images);
            ApplyDoctor(startup.Doctor);
            _hasUnresolvedRecovery = startup.Recovery.Any(result => !result.IsCleaned);
            if (_hasUnresolvedRecovery)
            {
                ErrorMessage = "이전 작업을 정리하지 못했습니다. 상태를 확인한 뒤 다시 시도하세요.";
                OnPropertyChanged(nameof(IsReady));
            }

            _isInitialized = true;
            SessionStatus = ComposeSessionStatus();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBusy = false;
            RaiseCommandState();
        }

        return Task.CompletedTask;
    }

    public void RequestSetup() => SetupRequested?.Invoke(this, EventArgs.Empty);

    public async Task RefreshImagesAsync()
    {
        ErrorMessage = null;
        try
        {
            LoadImages(await _runtime.ListImagesAsync(_shutdown.Token));
            SessionStatus = HasImages
                ? "서비스를 선택하세요."
                : "Windows 환경을 먼저 준비하세요.";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_runningSession is not null)
        {
            try
            {
                await _runningSession.DisposeAsync();
            }
            finally
            {
                _runningSession = null;
            }
        }

        foreach (var service in _allServices)
        {
            service.Dispose();
        }

        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    public void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Desktop catalog operation failed: {0}", exception);
        ErrorMessage = exception switch
        {
            LaunchPrerequisiteException => "실행 준비가 되지 않았습니다. 준비 상태를 확인하세요.",
            ImageVerificationException => "Windows 환경을 확인하지 못했습니다. 다시 준비하세요.",
            OperationCanceledException => "작업이 취소되었습니다.",
            _ => "작업 중 오류가 발생했습니다.",
        };
        SessionStatus = "완료하지 못했습니다.";
    }

    private async Task RefreshDoctorAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            ApplyDoctor(await _runtime.InspectHostAsync(_shutdown.Token));
            LoadImages(await _runtime.ListImagesAsync(_shutdown.Token));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LaunchSelectedAsync()
    {
        if (SelectedService is null || SelectedImage is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        var progress = new Progress<SessionState>(state =>
            Dispatcher.UIThread.Post(() => SessionStatus = StateText(state)));
        try
        {
            _runningSession = await _runtime.LaunchAsync(
                new LaunchRequest([SelectedService.Id]),
                SelectedImage.Id,
                progress,
                _shutdown.Token);
            OnPropertyChanged(nameof(IsSessionRunning));
            RaiseCommandState();
            _ = ObserveSessionAsync(_runningSession);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ObserveSessionAsync(IRunningLinuxClothSession session)
    {
        try
        {
            await session.Completion;
            SessionStatus = "닫았고 변경사항을 삭제했습니다.";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }

            if (ReferenceEquals(_runningSession, session))
            {
                _runningSession = null;
                OnPropertyChanged(nameof(IsSessionRunning));
                RaiseCommandState();
            }
        }
    }

    private async Task StopSessionAsync()
    {
        if (_runningSession is null)
        {
            return;
        }

        SessionStatus = "닫고 삭제하는 중…";
        await _runningSession.StopAsync(_shutdown.Token);
    }

    private void LoadCatalog(IReadOnlyList<CatalogServiceEntry> services)
    {
        foreach (var service in _allServices)
        {
            service.Dispose();
        }

        _allServices.Clear();
        _allServices.AddRange(services.Select(service => new ServiceCardViewModel(service)));
        Categories.Clear();
        Categories.Add(new CategoryFilterViewModel("전체", null));
        CatalogCategory[] categoryOrder =
        [
            CatalogCategory.Banking,
            CatalogCategory.CreditCard,
            CatalogCategory.Financing,
            CatalogCategory.Government,
            CatalogCategory.Insurance,
            CatalogCategory.Security,
            CatalogCategory.Education,
            CatalogCategory.Other,
        ];
        var availableCategories = _allServices
            .Select(service => service.CategoryValue)
            .ToHashSet();
        foreach (var category in categoryOrder.Where(availableCategories.Contains))
        {
            Categories.Add(new CategoryFilterViewModel(ServiceCardViewModel.CategoryName(category), category));
        }

        SelectedCategory = Categories[0];
        ApplyFilter();
    }

    private void LoadImages(IReadOnlyList<ManagedWindowsImage> images)
    {
        var previous = SelectedImage?.Id;
        Images.Clear();
        foreach (var image in images)
        {
            Images.Add(new ImageChoiceViewModel(
                image.ImageId,
                $"{image.ImageId.Value} · {image.Metadata.CreatedAt:yyyy-MM-dd}"));
        }

        SelectedImage = Images.FirstOrDefault(image => image.Id == previous) ?? Images.FirstOrDefault();
        OnPropertyChanged(nameof(HasImages));
    }

    private void ApplyDoctor(QemuDoctorResult result)
    {
        DoctorChecks.Clear();
        foreach (var check in result.Report.Checks)
        {
            DoctorChecks.Add(new DoctorCheckViewModel(
                CheckLabel(check.Name),
                check.IsAvailable,
                check.IsRequired,
                check.Detail));
        }

        _canHostLaunch = result.CanLaunch;
        var missing = result.Report.Checks.Count(check => check.IsRequired && !check.IsAvailable);
        DoctorSummary = result.CanLaunch
            ? "준비됨"
            : $"설정 {missing}개 필요";
        if (_isInitialized && _runningSession is null && !IsBusy)
        {
            SessionStatus = ComposeSessionStatus();
        }

        OnPropertyChanged(nameof(IsReady));
        RaiseCommandState();
    }

    private string ComposeSessionStatus()
    {
        if (_hasUnresolvedRecovery)
        {
            return "이전 작업 정리가 필요합니다";
        }

        if (!HasImages)
        {
            return IsReady
                ? "준비됨 · Windows 환경을 먼저 준비하세요"
                : "Windows 환경을 먼저 준비하세요";
        }

        return IsReady
            ? "준비됨 · 서비스를 선택하세요"
            : "서비스를 선택하세요";
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        var category = SelectedCategory?.Value;
        var selected = SelectedService;
        var filtered = _allServices.Where(service =>
            (category is null || service.CategoryValue == category) &&
            (query.Length == 0 ||
             service.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        FilteredServices.Clear();
        foreach (var service in filtered)
        {
            FilteredServices.Add(service);
        }

        SelectedService = selected is not null && FilteredServices.Contains(selected)
            ? selected
            : null;
    }

    private void RaiseCommandState()
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanConfigureImages));
        LaunchCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshDoctorCommand.RaiseCanExecuteChanged();
    }

    private static string StateText(SessionState state) => state switch
    {
        SessionState.Validating => "확인 중…",
        SessionState.PreparingOverlay => "환경 준비 중…",
        SessionState.PreparingConfigDisk => "실행 설정 준비 중…",
        SessionState.StartingNetwork => "연결 준비 중…",
        SessionState.StartingVm => "시작 중…",
        SessionState.WaitingForGuest => "준비 대기 중…",
        SessionState.Running => "서비스 실행 중",
        SessionState.Stopping => "종료 중…",
        SessionState.Cleaning => "삭제 중…",
        SessionState.Completed => "정리 완료",
        SessionState.Failed => "열지 못했습니다.",
        _ => "준비 중…",
    };

    private static string CheckLabel(string code) => code switch
    {
        QemuDoctorCheckCodes.Platform => "운영체제",
        QemuDoctorCheckCodes.Kvm => "가상화",
        QemuDoctorCheckCodes.Firmware => "Windows 시작",
        QemuDoctorCheckCodes.RuntimeDirectory => "작업 공간",
        QemuDoctorCheckCodes.RemoteViewer => "Windows 화면",
        QemuDoctorCheckCodes.Bubblewrap => "프로세스 격리",
        _ => code,
    };
}
