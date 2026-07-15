using Avalonia.Controls;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class MainWindow : UserControl
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    private void OnOpenImageSetup(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (DataContext is MainWindowViewModel viewModel && viewModel.CanConfigureImages)
        {
            viewModel.RequestSetup();
        }
    }
}
