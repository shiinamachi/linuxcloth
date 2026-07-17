using System.Diagnostics;
using Avalonia.Controls;
using LinuxCloth.Application.Setup;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.Setup;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class ShellWindow : Window, IAsyncDisposable
{
    private readonly FirstRunCoordinator _coordinator;
    private readonly DesktopRuntime _runtime;
    private bool _shutdownComplete;
    private bool _isConfirmingClose;
    private MainWindowViewModel? _mainViewModel;
    private SetupWizardViewModel? _setupViewModel;

    public ShellWindow()
    {
        InitializeComponent();
        _runtime = DesktopRuntime.CreateDefault();
        _coordinator = new FirstRunCoordinator(_runtime);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await RouteFromCurrentStateAsync();
    }

    private async Task RouteFromCurrentStateAsync()
    {
        StartupPanel.IsVisible = true;
        ShellContent.Content = null;
        try
        {
            var firstRun = await _coordinator.InitializeAsync();
            if (firstRun.Readiness.Route == SetupRoute.RecoveryRequired)
            {
                ShowRecovery(firstRun.Startup);
                return;
            }

            if (firstRun.Readiness.Route is SetupRoute.FirstRun or SetupRoute.EnvironmentRepair)
            {
                await ShowSetupAsync(firstRun);
                return;
            }

            await ShowMainAsync(firstRun.Startup);
        }
        catch (Exception exception)
        {
            ShowStartupFailure("시작 준비를 완료하지 못했습니다. 앱을 다시 실행하거나 로그를 확인하세요.", exception);
        }
    }

    private void ShowRecovery(DesktopStartupSnapshot startup)
    {
        var failures = startup.Recovery.Where(result => !result.IsCleaned).ToArray();
        var details = string.Join(
            Environment.NewLine,
            failures.Select(result =>
                $"• {result.SessionId}: {result.Detail ?? result.Failure?.Message ?? result.Disposition.ToString()}"));
        ShellContent.Content = new RecoveryView(details, async (_, _) => await RouteFromCurrentStateAsync());
        StartupPanel.IsVisible = false;
    }

    private async Task ShowSetupAsync(FirstRunSnapshot firstRun)
    {
        Width = 1000;
        Height = 760;
        MinWidth = 720;
        MinHeight = 480;
        if (_mainViewModel is not null)
        {
            _mainViewModel.SetupRequested -= OnSetupRequested;
            await _mainViewModel.DisposeAsync();
            _mainViewModel = null;
        }

        if (_setupViewModel is not null)
        {
            await DisposeSetupAsync();
        }

        _setupViewModel = new SetupWizardViewModel(
            _runtime,
            firstRun,
            new SetupStateStore(_runtime.Paths),
            new JsonSetupRunStore(_runtime.Paths.ConfigDirectory),
            new DistributionInfoReader(),
            new PackagePlanResolver(),
            new PackageKitPackageInstaller(new PackageKitDbusClient()),
            HostCapacityInspector.Inspect(_runtime.Paths.DataDirectory));
        _setupViewModel.Completed += OnSetupCompleted;
        _setupViewModel.LaterRequested += OnLaterRequested;
        ShellContent.Content = new SetupWizardView(_setupViewModel);
        StartupPanel.IsVisible = false;
        await _setupViewModel.InitializeAsync();
    }

    private async Task ShowMainAsync(DesktopStartupSnapshot startup)
    {
        Width = 1280;
        Height = 820;
        MinWidth = 720;
        MinHeight = 480;
        await DisposeSetupAsync();
        if (_mainViewModel is not null)
        {
            await _mainViewModel.DisposeAsync();
        }

        _mainViewModel = new MainWindowViewModel(_runtime);
        _mainViewModel.SetupRequested += OnSetupRequested;
        await _mainViewModel.InitializeAsync(startup);
        ShellContent.Content = new MainWindow(_mainViewModel);
        StartupPanel.IsVisible = false;
    }

    private async void OnSetupRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        StartupPanel.IsVisible = true;
        try
        {
            var firstRun = await _coordinator.InitializeAsync();
            if (firstRun.Readiness.Route == SetupRoute.RecoveryRequired)
            {
                ShowRecovery(firstRun.Startup);
            }
            else
            {
                await ShowSetupAsync(firstRun);
            }
        }
        catch (Exception exception)
        {
            ShowStartupFailure("초기 설정을 열지 못했습니다. 잠시 후 다시 시도하세요.", exception);
        }
    }

    private async void OnSetupCompleted(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await RouteFromCurrentStateAsync();
    }

    private async void OnLaterRequested(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        StartupPanel.IsVisible = true;
        try
        {
            var firstRun = await _coordinator.InitializeAsync();
            await ShowMainAsync(firstRun.Startup);
        }
        catch (Exception exception)
        {
            ShowStartupFailure("서비스 화면을 열지 못했습니다. 잠시 후 다시 시도하세요.", exception);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _ = sender;
        if (_shutdownComplete)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (_isConfirmingClose)
        {
            return;
        }

        if (_setupViewModel?.HasActiveOperation == true)
        {
            _isConfirmingClose = true;
            try
            {
                var confirmed = await new ActiveOperationCloseDialog().ShowDialog<bool>(this);
                if (!confirmed)
                {
                    return;
                }
            }
            finally
            {
                _isConfirmingClose = false;
            }
        }

        try
        {
            await DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdownComplete)
        {
            return;
        }

        if (_mainViewModel is not null)
        {
            _mainViewModel.SetupRequested -= OnSetupRequested;
            await _mainViewModel.DisposeAsync();
            _mainViewModel = null;
        }

        await DisposeSetupAsync();

        await _runtime.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeSetupAsync()
    {
        if (_setupViewModel is null)
        {
            return;
        }

        _setupViewModel.Completed -= OnSetupCompleted;
        _setupViewModel.LaterRequested -= OnLaterRequested;
        await _setupViewModel.DisposeAsync();
        _setupViewModel = null;
    }

    private void ShowStartupFailure(string userMessage, Exception exception)
    {
        Trace.TraceError("Desktop startup failure: {0}", exception);
        StartupStatus.Text = userMessage;
    }
}
