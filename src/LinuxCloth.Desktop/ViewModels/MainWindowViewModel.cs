using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Localization;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly List<ServiceCardViewModel> _allServices = [];
    private readonly DesktopRuntime _runtime;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _canHostLaunch;
    private string _doctorSummary;
    private string? _errorMessage;
    private bool _hasUnresolvedRecovery;
    private bool _isBusy;
    private bool _isInitialized;
    private QemuDoctorResult? _lastDoctor;
    private IRunningLinuxClothSession? _runningSession;
    private string _searchText = string.Empty;
    private CategoryFilterViewModel? _selectedCategory;
    private ImageChoiceViewModel? _selectedImage;
    private ServiceCardViewModel? _selectedService;
    private string _sessionStatus;

    public MainWindowViewModel(DesktopRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _doctorSummary = Text("Catalog.Status.Checking");
        _sessionStatus = Text("Catalog.Status.ChooseService");
        Categories.Add(new CategoryFilterViewModel(Text("Catalog.Category.All"), null));
        SelectedCategory = Categories[0];
        UiStrings.Instance.CultureChanged += OnCultureChanged;
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
        SessionStatus = Text("Catalog.Status.GettingReady");
        try
        {
            LoadCatalog(startup.Catalog.Services);
            LoadImages(startup.Images);
            ApplyDoctor(startup.Doctor);
            _hasUnresolvedRecovery = startup.Recovery.Any(result => !result.IsCleaned);
            if (_hasUnresolvedRecovery)
            {
                ErrorMessage = Text("Catalog.Error.Recovery");
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
                ? Text("Catalog.Status.ChooseService")
                : Text("Catalog.Status.PrepareEnvironment");
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

        UiStrings.Instance.CultureChanged -= OnCultureChanged;
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    public void ShowError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Desktop catalog operation failed: {0}", exception);
        ErrorMessage = exception switch
        {
            LaunchPrerequisiteException => Text("Catalog.Error.Prerequisite"),
            ImageVerificationException => Text("Catalog.Error.Image"),
            OperationCanceledException => Text("Catalog.Error.Canceled"),
            _ => Text("Catalog.Error.Generic"),
        };
        SessionStatus = Text("Catalog.Status.Failed");
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
            SessionStatus = Text("Catalog.Status.Deleted");
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

        SessionStatus = Text("Catalog.Status.Deleting");
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
        RebuildCategories();
    }

    private void RebuildCategories(CatalogCategory? selectedCategory = null)
    {
        Categories.Clear();
        Categories.Add(new CategoryFilterViewModel(Text("Catalog.Category.All"), null));
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

        SelectedCategory = Categories.FirstOrDefault(category => category.Value == selectedCategory) ??
                           Categories[0];
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
        _lastDoctor = result;
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
            ? Text("Catalog.Status.Ready")
            : UiStrings.Instance.Format("Catalog.Status.SetupNeeded", missing);
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
            return Text("Catalog.Status.RecoveryNeeded");
        }

        if (!HasImages)
        {
            return IsReady
                ? Text("Catalog.Status.ReadyPrepareEnvironment")
                : Text("Catalog.Status.PrepareEnvironment");
        }

        return IsReady
            ? Text("Catalog.Status.ReadyChooseService")
            : Text("Catalog.Status.ChooseService");
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

    private void OnCultureChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var selectedCategory = SelectedCategory?.Value;
        RebuildCategories(selectedCategory);
        if (_lastDoctor is not null)
        {
            ApplyDoctor(_lastDoctor);
        }
        else
        {
            DoctorSummary = Text("Catalog.Status.Checking");
        }

        SessionStatus = IsSessionRunning
            ? Text("Catalog.Session.Running")
            : ComposeSessionStatus();
        if (HasError)
        {
            ErrorMessage = Text("Catalog.Error.Generic");
        }
    }

    private static string StateText(SessionState state) => state switch
    {
        SessionState.Validating => Text("Catalog.Session.Validating"),
        SessionState.PreparingOverlay => Text("Catalog.Session.Preparing"),
        SessionState.PreparingConfigDisk => Text("Catalog.Session.Config"),
        SessionState.StartingNetwork => Text("Catalog.Session.Network"),
        SessionState.StartingVm => Text("Catalog.Session.Starting"),
        SessionState.WaitingForGuest => Text("Catalog.Session.Waiting"),
        SessionState.Running => Text("Catalog.Session.Running"),
        SessionState.Stopping => Text("Catalog.Session.Stopping"),
        SessionState.Cleaning => Text("Catalog.Session.Cleaning"),
        SessionState.Completed => Text("Catalog.Session.Completed"),
        SessionState.Failed => Text("Catalog.Session.Failed"),
        _ => Text("Catalog.Status.GettingReady"),
    };

    private static string CheckLabel(string code) => code switch
    {
        QemuDoctorCheckCodes.Platform => Text("Catalog.Check.Platform"),
        QemuDoctorCheckCodes.Kvm => Text("Catalog.Check.Virtualization"),
        QemuDoctorCheckCodes.Firmware => Text("Catalog.Check.Firmware"),
        QemuDoctorCheckCodes.RuntimeDirectory => Text("Catalog.Check.Workspace"),
        QemuDoctorCheckCodes.RemoteViewer => Text("Catalog.Check.Display"),
        QemuDoctorCheckCodes.Bubblewrap => Text("Catalog.Check.Isolation"),
        _ => code,
    };

    private static string Text(string key) => UiStrings.Instance[key];
}
