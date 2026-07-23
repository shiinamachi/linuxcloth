using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Desktop.Localization;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.ViewModels;
using LinuxCloth.Desktop.Views;

namespace LinuxCloth.Desktop.Tests;

[Collection(HeadlessUiTestGroup.Name)]
public sealed class MainWindowHeadlessTests
{
    private readonly HeadlessUnitTestSession _session;

    public MainWindowHeadlessTests(HeadlessUiFixture fixture)
    {
        _session = fixture.Session;
    }

    [Fact]
    public async Task SwitchesBetweenCompactMediumAndWideLayouts()
    {
        await _session.Dispatch(
            () =>
            {
                var compact = ShowAt(720, 480);
                Assert.True(compact.View.FindControl<Border>("CompactHeader")!.IsVisible);
                Assert.False(compact.View.FindControl<Border>("DesktopHeader")!.IsVisible);
                Assert.False(compact.View.FindControl<Border>("CategoryRail")!.IsVisible);
                Assert.False(compact.View.FindControl<Border>("WideDetailsPanel")!.IsVisible);
                Assert.Equal(
                    new Thickness(8, 12, 8, 8),
                    compact.View.FindControl<Grid>("ServicesContent")!.Margin);
                compact.Window.Close();

                var medium = ShowAt(1000, 640);
                Assert.False(medium.View.FindControl<Border>("CompactHeader")!.IsVisible);
                Assert.True(medium.View.FindControl<Border>("DesktopHeader")!.IsVisible);
                Assert.False(medium.View.FindControl<Border>("CategoryRail")!.IsVisible);
                Assert.True(medium.View.FindControl<Grid>("AdaptiveFilterBar")!.IsVisible);
                Assert.Equal(
                    new Thickness(20, 16, 20, 12),
                    medium.View.FindControl<Grid>("ServicesContent")!.Margin);
                medium.Window.Close();

                var wide = ShowAt(1280, 720);
                Assert.True(wide.View.FindControl<Border>("CategoryRail")!.IsVisible);
                Assert.True(wide.View.FindControl<Border>("WideDetailsPanel")!.IsVisible);
                Assert.False(wide.View.FindControl<Grid>("AdaptiveFilterBar")!.IsVisible);
                var contentGrid = wide.View.FindControl<Grid>("ContentGrid")!;
                Assert.Equal(new GridLength(208), contentGrid.ColumnDefinitions[0].Width);
                Assert.Equal(new GridLength(368), contentGrid.ColumnDefinitions[2].Width);
                Assert.Equal(
                    new Thickness(24, 20, 24, 12),
                    wide.View.FindControl<Grid>("ServicesContent")!.Margin);
                wide.Window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task UpdatesCatalogCopyWhenLanguageChanges()
    {
        await _session.Dispatch(
            () =>
            {
                var strings = UiStrings.Instance;
                var originalCulture = strings.SelectedLanguage.CultureName;
                var (window, view) = ShowAt(1280, 720);
                try
                {
                    strings.SelectCulture("ko-KR");
                    Assert.Equal("서비스 검색", view.FindControl<TextBox>("DesktopSearchBox")!.PlaceholderText);

                    strings.SelectCulture("en-US");
                    Assert.Equal("Search services", view.FindControl<TextBox>("DesktopSearchBox")!.PlaceholderText);
                }
                finally
                {
                    strings.SelectCulture(originalCulture);
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task RendersCompactLayoutAtTwoHundredPercentScaling()
    {
        await _session.Dispatch(
            () =>
            {
                var view = new MainWindow();
                var window = new Window
                {
                    Width = 720,
                    Height = 480,
                    Content = view,
                };
                window.Show();
                try
                {
                    window.SetRenderScaling(2);
                    Assert.Equal(2, window.RenderScaling);
                    Assert.Equal(720, window.ClientSize.Width);
                    Assert.Equal(480, window.ClientSize.Height);
                    Assert.True(view.FindControl<Border>("CompactHeader")!.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Theory]
    [InlineData(720, 480, 1.0)]
    [InlineData(960, 540, 1.25)]
    [InlineData(1280, 720, 1.5)]
    [InlineData(1440, 900, 2.0)]
    public async Task PreservesLogicalLayoutAcrossSupportedSizesAndScales(
        double width,
        double height,
        double renderScaling)
    {
        await _session.Dispatch(
            () =>
            {
                var (window, view) = ShowAt(width, height);
                try
                {
                    window.SetRenderScaling(renderScaling);
                    Assert.Equal(renderScaling, window.RenderScaling);
                    Assert.Equal(width, window.ClientSize.Width);
                    Assert.Equal(height, window.ClientSize.Height);
                    Assert.True(view.FindControl<Grid>("ContentGrid")!.Bounds.Width <= width);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task EscapeClosesMediumDetailsDrawerAndReturnsFocusToServices()
    {
        await using var runtime = DesktopRuntime.CreateDefault();
        await using var viewModel = new MainWindowViewModel(runtime);
        using var service = CreateService();

        await _session.Dispatch(
            () =>
            {
                var view = new MainWindow(viewModel);
                var window = new Window
                {
                    Width = 1000,
                    Height = 640,
                    Content = view,
                };
                window.Show();
                try
                {
                    window.Activate();
                    viewModel.FilteredServices.Add(service);
                    viewModel.SelectedService = service;
                    Assert.True(view.FindControl<Border>("DetailsOverlay")!.IsVisible);
                    Assert.True(view.FindControl<Button>("CloseDetailsButton")!.IsFocused);

                    window.KeyPress(
                        Key.Escape,
                        RawInputModifiers.None,
                        PhysicalKey.Escape,
                        keySymbol: null);

                    Assert.False(view.FindControl<Border>("DetailsOverlay")!.IsVisible);
                    Assert.Null(viewModel.SelectedService);
                    Assert.True(view.FindControl<ListBox>("ServicesList")!.IsKeyboardFocusWithin);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task ControlFMovesFocusToServiceSearch()
    {
        await _session.Dispatch(
            () =>
            {
                var (window, view) = ShowAt(1280, 720);
                try
                {
                    window.Activate();
                    Assert.True(view.FindControl<Button>("ReadinessButton")!.Focus());
                    window.KeyPress(
                        Key.F,
                        RawInputModifiers.Control,
                        PhysicalKey.F,
                        keySymbol: null);

                    Assert.True(view.FindControl<TextBox>("DesktopSearchBox")!.IsFocused);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    private static (Window Window, MainWindow View) ShowAt(double width, double height)
    {
        var view = new MainWindow();
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
        };
        window.Show();
        return (window, view);
    }

    private static ServiceCardViewModel CreateService()
    {
        var id = ServiceId.Parse("test-service");
        var service = new CatalogService(
            id,
            "테스트 서비스",
            "Test service",
            CatalogCategory.Other,
            new Uri("https://example.com"),
            CompatNotes: null,
            EnglishCompatNotes: null,
            SearchKeywords: [],
            Packages: [],
            EdgeExtensions: [],
            CustomBootstrap: null);
        var compatibility = new CompatibilityRecord(
            id,
            CompatibilityStatus.Verified,
            DisplayMode.Spice,
            TestedWindowsBuild: null,
            TestedSporkVersion: null,
            TestedCatalogCommit: null,
            TestedQemuVersion: null,
            KnownIssues: [],
            LastVerifiedAt: null);
        return new ServiceCardViewModel(new CatalogServiceEntry(service, compatibility, Image: null));
    }
}
