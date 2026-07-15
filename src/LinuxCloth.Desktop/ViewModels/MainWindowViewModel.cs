using System.Collections.ObjectModel;
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
    private string _doctorSummary = "시스템 검사 대기 중";
    private string? _errorMessage;
    private bool _hasUnresolvedRecovery;
    private bool _isBusy;
    private bool _isInitialized;
    private IRunningLinuxClothSession? _runningSession;
    private string _searchText = string.Empty;
    private CategoryFilterViewModel? _selectedCategory;
    private ImageChoiceViewModel? _selectedImage;
    private ServiceCardViewModel? _selectedService;
    private string _sessionStatus = "일회용 Windows 세션을 시작할 준비가 되었습니다.";

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
        SessionStatus = "카탈로그와 시스템 상태를 준비하고 있습니다…";
        try
        {
            LoadCatalog(startup.Catalog.Services);
            LoadImages(startup.Images);
            ApplyDoctor(startup.Doctor);
            _hasUnresolvedRecovery = startup.Recovery.Any(result => !result.IsCleaned);
            if (_hasUnresolvedRecovery)
            {
                ErrorMessage = "자동 복구하지 못한 이전 세션이 있습니다. CLI cleanup 결과를 확인한 뒤 실행하세요.";
            }

            _isInitialized = true;
            SessionStatus = HasImages
                ? "서비스와 Windows 이미지를 선택하세요."
                : "등록된 Windows 이미지가 없습니다. 먼저 Windows 11 기준 이미지를 준비하세요.";
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
                ? "서비스와 Windows 이미지를 선택하세요."
                : "등록된 Windows 이미지가 없습니다. 기준 이미지 준비를 시작하세요.";
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
        ErrorMessage = exception switch
        {
            LaunchPrerequisiteException => "필수 가상화 구성 요소가 준비되지 않았습니다. 시스템 검사 결과를 확인하세요.",
            ImageVerificationException => "선택한 Windows 이미지의 무결성 검사가 실패했습니다.",
            OperationCanceledException => "작업이 취소되었습니다.",
            _ => exception.Message,
        };
        SessionStatus = "작업을 완료하지 못했습니다.";
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
            SessionStatus = "세션이 종료되었고 일회용 데이터가 정리되었습니다.";
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

        SessionStatus = "Windows를 종료하고 세션 데이터를 정리하고 있습니다…";
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
            ? "KVM 일회용 Windows 실행 준비 완료"
            : $"필수 항목 {missing}개를 준비해야 합니다";
        RaiseCommandState();
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
            : FilteredServices.FirstOrDefault();
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
        SessionState.Validating => "카탈로그와 이미지 무결성을 확인하고 있습니다…",
        SessionState.PreparingOverlay => "일회용 Windows 디스크를 만들고 있습니다…",
        SessionState.PreparingConfigDisk => "공식 TableCloth 실행 설정을 준비하고 있습니다…",
        SessionState.StartingNetwork => "격리된 네트워크를 시작하고 있습니다…",
        SessionState.StartingVm => "Windows를 시작하고 있습니다…",
        SessionState.WaitingForGuest => "Windows와 GuestBridge 준비를 기다리고 있습니다…",
        SessionState.Running => "Windows에서 TableCloth 실행 준비를 시작했습니다.",
        SessionState.Stopping => "Windows를 안전하게 종료하고 있습니다…",
        SessionState.Cleaning => "일회용 세션 데이터를 삭제하고 있습니다…",
        SessionState.Completed => "세션 정리가 완료되었습니다.",
        SessionState.Failed => "세션 실행에 실패했습니다.",
        _ => "준비 중…",
    };

    private static string CheckLabel(string code) => code switch
    {
        QemuDoctorCheckCodes.Platform => "Linux x86_64",
        QemuDoctorCheckCodes.Kvm => "KVM 접근",
        QemuDoctorCheckCodes.Firmware => "Secure Boot OVMF",
        QemuDoctorCheckCodes.RuntimeDirectory => "런타임 소켓",
        QemuDoctorCheckCodes.RemoteViewer => "SPICE 뷰어",
        QemuDoctorCheckCodes.Bubblewrap => "QEMU 격리",
        _ => code,
    };
}
