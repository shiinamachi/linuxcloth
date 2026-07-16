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

        ContentGrid.ColumnDefinitions[0].Width = new GridLength(isWide ? 200 : 0);
        ContentGrid.ColumnDefinitions[2].Width = new GridLength(isWide ? 340 : 0);
        ServicesContent.Margin = isWide
            ? new Thickness(24, 20)
            : isCompact
                ? new Thickness(12, 14)
                : new Thickness(18, 18);

        var hasSelectedService = _subscribedViewModel?.HasSelectedService == true;
        DetailsOverlay.IsVisible = !isWide && hasSelectedService;
        DetailsDrawer.Width = isCompact ? double.NaN : 400;
        DetailsDrawer.MaxWidth = isCompact ? double.PositiveInfinity : 400;
        DetailsDrawer.Margin = isCompact ? new Thickness(0) : new Thickness(16);
        DetailsDrawer.CornerRadius = isCompact ? new CornerRadius(0) : new CornerRadius(16);
        DetailsDrawer.HorizontalAlignment = isCompact
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
    }
}
