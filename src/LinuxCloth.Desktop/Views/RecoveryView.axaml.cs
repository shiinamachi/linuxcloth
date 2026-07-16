using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LinuxCloth.Desktop.Views;

public sealed partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
    }

    public RecoveryView(string technicalDetails, EventHandler<RoutedEventArgs> retryHandler)
        : this()
    {
        DetailsText.Text = technicalDetails;
        RetryButton.Click += retryHandler;
    }
}
