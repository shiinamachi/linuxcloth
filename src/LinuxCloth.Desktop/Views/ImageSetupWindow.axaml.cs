using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class ImageSetupWindow : Window
{
    private static readonly FilePickerFileType IsoFileType = new("ISO 디스크 이미지")
    {
        Patterns = ["*.iso", "*.ISO"],
    };

    private static readonly FilePickerFileType FirmwareFileType = new("UEFI 펌웨어 이미지")
    {
        Patterns = ["*.fd", "*.bin", "*.rom"],
    };

    private static readonly FilePickerFileType WindowsExecutableFileType = new("Windows 실행 파일")
    {
        Patterns = ["*.exe", "*.EXE"],
    };

    private bool _shutdownComplete;

    public ImageSetupWindow()
    {
        InitializeComponent();
    }

    public ImageSetupWindow(ImageSetupViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private ImageSetupViewModel ViewModel =>
        (ImageSetupViewModel)(DataContext ?? throw new InvalidOperationException("이미지 설정 화면의 데이터가 없습니다."));

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await ViewModel.InitializeAsync();
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
            await ViewModel.DisposeAsync();
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }

    private async void OnBrowseWindowsIso(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await PickFileAsync("Windows 11 x64 ISO 선택", IsoFileType, path => ViewModel.WindowsIsoPath = path);
    }

    private async void OnBrowseVirtioIso(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await PickFileAsync("virtio-win ISO 선택", IsoFileType, path => ViewModel.VirtioWinIsoPath = path);
    }

    private async void OnBrowseOvmfCode(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await PickFileAsync("Secure Boot OVMF 코드 선택", FirmwareFileType, path => ViewModel.OvmfCodePath = path);
    }

    private async void OnBrowseOvmfVariables(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await PickFileAsync("OVMF 변수 템플릿 선택", FirmwareFileType, path => ViewModel.OvmfVariablesTemplatePath = path);
    }

    private async void OnBrowseGuestBridge(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        await PickFileAsync("고정된 linuxcloth GuestBridge 선택", WindowsExecutableFileType, path => ViewModel.GuestBridgeExecutablePath = path);
    }

    private async void OnBrowseStagingDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "보존된 이미지 빌드 스테이징 디렉터리 선택",
                    AllowMultiple = false,
                });
            var path = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                ViewModel.StagingDirectory = path;
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportPickerError(exception);
        }
    }

    private void OnClose(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close();
    }

    private async Task PickFileAsync(string title, FilePickerFileType fileType, Action<string> assign)
    {
        ArgumentNullException.ThrowIfNull(assign);
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                    FileTypeFilter = [fileType],
                });
            var path = files.Count == 0 ? null : files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                assign(path);
            }
        }
        catch (Exception exception)
        {
            ViewModel.ReportPickerError(exception);
        }
    }
}
