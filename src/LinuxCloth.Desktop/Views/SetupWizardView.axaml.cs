using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LinuxCloth.Desktop.Localization;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class SetupWizardView : UserControl
{
    private static FilePickerFileType IsoFileType => new(UiStrings.Instance["Setup.FilePicker.Type"])
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
        var path = await PickIsoAsync(UiStrings.Instance["Setup.FilePicker.WindowsTitle"]);
        if (path is not null)
        {
            await ViewModel.ValidateWindowsMediaAsync(path);
        }
    }

    private async void OnBrowseVirtioIso(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        var path = await PickIsoAsync(UiStrings.Instance["Setup.FilePicker.DriversTitle"]);
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
            ? new Thickness(12, 12, 12, 24)
            : new Thickness(32, 16, 32, 32);
        ReadyHeader.Padding = isCompact
            ? new Thickness(16, 56, 16, 12)
            : new Thickness(64, 32, 168, 16);
    }
}
