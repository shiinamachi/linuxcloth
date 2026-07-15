using Avalonia.Controls;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(DesktopRuntime.CreateDefault());
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _ = sender;
        if (_shutdownComplete || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        eventArgs.Cancel = true;
        try
        {
            await viewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            viewModel.ShowError(exception);
        }
        finally
        {
            _shutdownComplete = true;
            Close();
        }
    }
}
