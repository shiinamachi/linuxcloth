using Avalonia.Controls;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Controls;

public sealed partial class ServiceDetails : UserControl
{
    public ServiceDetails()
    {
        InitializeComponent();
    }

    private void OnOpenSetup(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (DataContext is MainWindowViewModel viewModel && viewModel.CanConfigureImages)
        {
            viewModel.RequestSetup();
        }
    }
}
