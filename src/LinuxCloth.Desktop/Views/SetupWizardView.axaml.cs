using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class SetupWizardView : UserControl
{
    private static readonly FilePickerFileType IsoFileType = new("ISO 디스크 이미지")
    {
        Patterns = ["*.iso", "*.ISO"],
    };

    public SetupWizardView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        AttachedToVisualTree += (_, _) => ApplyResponsiveLayout();
    }

    public SetupWizardView(SetupWizardViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    private SetupWizardViewModel ViewModel =>
        (SetupWizardViewModel)(DataContext ?? throw new InvalidOperationException("초기 설정 데이터가 없습니다."));

    private async void OnBrowseWindowsIso(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var path = await PickIsoAsync("Windows 11 x64 ISO 선택");
        if (path is not null)
        {
            await ViewModel.ValidateWindowsMediaAsync(path);
        }
    }

    private async void OnBrowseVirtioIso(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var path = await PickIsoAsync("Windows 드라이버 ISO 선택");
        if (path is not null)
        {
            await ViewModel.ValidateVirtioMediaAsync(path);
        }
    }

    private async void OnOpenWindowsDownload(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await LaunchAsync(SetupWizardViewModel.WindowsDownloadUri);
    }

    private async void OnCopyManualCommand(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ??
                            throw new InvalidOperationException("클립보드를 사용할 수 없습니다.");
            await clipboard.SetTextAsync(ViewModel.ManualInstallCommand);
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalActionError(exception);
        }
    }

    private async Task<string?> PickIsoAsync(string title)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ??
                          throw new InvalidOperationException("파일 선택기를 사용할 수 없습니다.");
            var files = await storage.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = [IsoFileType],
                });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalActionError(exception);
            return null;
        }
    }

    private async Task LaunchAsync(Uri uri)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is not ILauncher launcher || !await launcher.LaunchUriAsync(uri))
            {
                throw new InvalidOperationException("기본 브라우저를 열 수 없습니다.");
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportExternalActionError(exception);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName == nameof(SetupWizardViewModel.ErrorMessage) && ViewModel.HasError)
        {
            Dispatcher.UIThread.Post(() => ErrorSummary.Focus());
        }
    }

    private void ApplyResponsiveLayout()
    {
        var width = Bounds.Width > 0 ? Bounds.Width : 980;
        var isCompact = width < 900;
        ContentFrame.Margin = isCompact
            ? new Thickness(12, 10)
            : new Thickness(20, 16);
    }
}
