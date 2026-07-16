using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LinuxCloth.Desktop.Views;

public sealed partial class ActiveOperationCloseDialog : Window
{
    public ActiveOperationCloseDialog()
    {
        InitializeComponent();
    }

    private void OnKeepWorking(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(false);
    }

    private void OnStopAndClose(object? sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        Close(true);
    }
}
