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
    private MainWindowViewModel? _mainViewModel;

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
                ShowSetup(firstRun);
                return;
            }

            await ShowMainAsync(firstRun.Startup);
        }
        catch (Exception exception)
        {
            StartupStatus.Text = $"시작 준비를 완료하지 못했습니다. {exception.Message}";
        }
    }

    private void ShowRecovery(DesktopStartupSnapshot startup)
    {
        var failures = startup.Recovery.Where(result => !result.IsCleaned).ToArray();
        var details = string.Join(
            Environment.NewLine,
            failures.Select(result =>
                $"• {result.SessionId}: {result.Detail ?? result.Failure?.Message ?? result.Disposition.ToString()}"));
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
                    new TextBlock { Text = "이전 세션 복구가 필요합니다", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock { Text = "일회용 데이터의 안전한 정리가 끝날 때까지 새 세션이나 이미지 생성을 시작할 수 없습니다.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = details, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    CreateRetryButton(),
                },
            },
        };
        StartupPanel.IsVisible = false;
    }

    private Button CreateRetryButton()
    {
        var button = new Button { Content = "복구 다시 시도", Classes = { "primary" } };
        button.Click += async (_, _) => await RouteFromCurrentStateAsync();
        return button;
    }

    private void ShowSetup(FirstRunSnapshot firstRun)
    {
        var placeholder = new StackPanel
        {
            MaxWidth = 720,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "linuxcloth 초기 설정", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = firstRun.Readiness.HasVerifiedImage ? "환경 복구가 필요합니다." : "첫 실행 설정이 필요합니다." },
            },
        };
        ShellContent.Content = placeholder;
        StartupPanel.IsVisible = false;
    }

    private async Task ShowMainAsync(DesktopStartupSnapshot startup)
    {
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
        await RouteFromCurrentStateAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _ = sender;
        if (_shutdownComplete)
        {
            return;
        }

        eventArgs.Cancel = true;
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

        await _runtime.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
