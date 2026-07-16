using Avalonia.Controls;
using Avalonia.Headless;
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
                compact.Window.Close();

                var medium = ShowAt(1000, 640);
                Assert.False(medium.View.FindControl<Border>("CompactHeader")!.IsVisible);
                Assert.True(medium.View.FindControl<Border>("DesktopHeader")!.IsVisible);
                Assert.False(medium.View.FindControl<Border>("CategoryRail")!.IsVisible);
                Assert.True(medium.View.FindControl<Grid>("AdaptiveFilterBar")!.IsVisible);
                medium.Window.Close();

                var wide = ShowAt(1280, 720);
                Assert.True(wide.View.FindControl<Border>("CategoryRail")!.IsVisible);
                Assert.True(wide.View.FindControl<Border>("WideDetailsPanel")!.IsVisible);
                Assert.False(wide.View.FindControl<Grid>("AdaptiveFilterBar")!.IsVisible);
                wide.Window.Close();
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
