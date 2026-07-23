using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using LinuxCloth.Desktop.Localization;
using LinuxCloth.Desktop.Views;

namespace LinuxCloth.Desktop.Tests;

[Collection(HeadlessUiTestGroup.Name)]
public sealed class MainWindowHeadlessTests
{
    [Fact]
    public async Task SwitchesBetweenCompactMediumAndWideLayouts()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
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
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
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
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
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

    [Fact]
    public async Task ControlFMovesFocusToServiceSearch()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
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
}
