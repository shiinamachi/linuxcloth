using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
    }

    public SetupWizardView(SetupWizardViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
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
        var path = await PickIsoAsync("virtio-win ISO 선택");
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

    private async void OnOpenVirtioDownload(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await LaunchAsync(SetupWizardViewModel.VirtioDownloadUri);
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
}
