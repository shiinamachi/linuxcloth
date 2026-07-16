using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Views;

public sealed partial class MainWindow : UserControl
{
    private const double CompactBreakpoint = 820;
    private const double WideBreakpoint = 1180;
    private MainWindowViewModel? _subscribedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        DataContextChanged += (_, _) => SubscribeToViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            SubscribeToViewModel();
            ApplyResponsiveLayout();
        };
        DetachedFromVisualTree += (_, _) => UnsubscribeFromViewModel();
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

    private void OnCloseDetails(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        CloseAdaptiveDetails();
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.Key == Key.Escape && DetailsOverlay.IsVisible)
        {
            CloseAdaptiveDetails();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key is Key.F or Key.K &&
            eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            (CompactHeader.IsVisible ? CompactSearchBox : DesktopSearchBox).Focus();
            eventArgs.Handled = true;
        }
    }

    private void CloseAdaptiveDetails()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectedService = null;
        }

        ServicesList.Focus();
    }

    private void SubscribeToViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext))
        {
            return;
        }

        UnsubscribeFromViewModel();
        if (DataContext is MainWindowViewModel viewModel)
        {
            _subscribedViewModel = viewModel;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyResponsiveLayout();
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is null)
        {
            return;
        }

        _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is nameof(MainWindowViewModel.SelectedService) or
            nameof(MainWindowViewModel.HasSelectedService))
        {
            ApplyResponsiveLayout();
        }
    }

    private void ApplyResponsiveLayout()
    {
        var width = Bounds.Width;
        if (width <= 0)
        {
            width = this.FindAncestorOfType<Window>()?.ClientSize.Width ?? WideBreakpoint;
        }

        var isWide = width >= WideBreakpoint;
        var isCompact = width < CompactBreakpoint;
        DesktopHeader.IsVisible = !isCompact;
        CompactHeader.IsVisible = isCompact;
        CategoryRail.IsVisible = isWide;
        WideDetailsPanel.IsVisible = isWide;
        AdaptiveFilterBar.IsVisible = !isWide && !isCompact;

        ContentGrid.ColumnDefinitions[0].Width = new GridLength(isWide ? 248 : 0);
        ContentGrid.ColumnDefinitions[2].Width = new GridLength(isWide ? 380 : 0);
        ServicesContent.Margin = isWide
            ? new Thickness(28, 24)
            : isCompact
                ? new Thickness(14, 16)
                : new Thickness(22, 20);

        var hasSelectedService = _subscribedViewModel?.HasSelectedService == true;
        DetailsOverlay.IsVisible = !isWide && hasSelectedService;
        DetailsDrawer.Width = isCompact ? double.NaN : 420;
        DetailsDrawer.MaxWidth = isCompact ? double.PositiveInfinity : 420;
        DetailsDrawer.Margin = isCompact ? new Thickness(0) : new Thickness(18);
        DetailsDrawer.CornerRadius = isCompact ? new CornerRadius(0) : new CornerRadius(22);
        DetailsDrawer.HorizontalAlignment = isCompact
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
    }
}
