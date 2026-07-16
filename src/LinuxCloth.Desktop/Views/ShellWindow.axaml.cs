using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
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
        var technicalDetails = new Expander
        {
            Header = "기술 세부정보",
            Content = new TextBlock { Text = details, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        };
        AutomationProperties.SetAutomationId(technicalDetails, "Recovery.TechnicalDetails");
        ShellContent.Content = new Border
        {
            Padding = new Avalonia.Thickness(48),
            Child = new StackPanel
            {
                MaxWidth = 720,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "이전 작업 정리가 필요합니다", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = "이전 Windows 환경의 안전한 정리가 끝날 때까지 새 서비스를 열거나 환경을 만들 수 없습니다.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    technicalDetails,
                    CreateRetryButton(),
                },
            },
        };
        StartupPanel.IsVisible = false;
    }

    private Button CreateRetryButton()
    {
        var button = new Button { Content = "복구 다시 시도", Classes = { "primary" } };
        AutomationProperties.SetAutomationId(button, "Recovery.Retry");
        button.Click += async (_, _) => await RouteFromCurrentStateAsync();
        return button;
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

internal sealed class ActiveOperationCloseDialog : Window
{
    public ActiveOperationCloseDialog()
    {
        Title = "linuxcloth — 작업 중단 확인";
        Width = 480;
        Height = 250;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "계속 작업", Classes = { "secondary" } };
        AutomationProperties.SetAutomationId(cancel, "ActiveOperation.KeepWorking");
        cancel.Click += (_, _) => Close(false);
        var stop = new Button { Content = "안전하게 중단하고 닫기", Classes = { "primary" } };
        AutomationProperties.SetAutomationId(stop, "ActiveOperation.StopAndClose");
        stop.Click += (_, _) => Close(true);
        Content = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("*,Auto"),
            Margin = new Avalonia.Thickness(26),
            Children =
            {
                new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "진행 중인 작업을 중단할까요?",
                            FontSize = 21,
                            FontWeight = Avalonia.Media.FontWeight.Bold,
                        },
                        new TextBlock
                        {
                            Text = "설치는 현재 시스템 상태를 따릅니다. Windows 환경 만들기는 안전하게 중단하고 나중에 다시 시작할 수 있도록 현재 상태를 보존합니다.",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        },
                    },
                },
                new StackPanel
                {
                    [Grid.RowProperty] = 1,
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, stop },
                },
            },
        };
    }
}
