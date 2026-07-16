using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using LinuxCloth.Desktop.Views;

namespace LinuxCloth.Desktop.Tests;

[Collection(HeadlessUiTestGroup.Name)]
public sealed class SetupWizardHeadlessTests
{
    [Fact]
    public async Task RendersKeyboardAccessibleWizardWithoutManagedComponentPickers()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
            () =>
            {
                var view = new SetupWizardView();
                var window = new Window
                {
                    Width = 980,
                    Height = 760,
                    Content = view,
                };
                window.Show();
                try
                {
                    var buttons = view.GetLogicalDescendants().OfType<Button>().ToArray();
                    var labels = buttons
                        .Select(button => button.Content?.ToString() ?? string.Empty)
                        .ToArray();
                    Assert.Contains("ISO 파일 선택", labels);
                    Assert.Contains("드라이버 ISO 선택", labels);
                    Assert.DoesNotContain(labels, label => label.Contains("OVMF", StringComparison.Ordinal));
                    Assert.DoesNotContain(labels, label => label.Contains("GuestBridge", StringComparison.Ordinal));

                    var mediaButtons = buttons.Where(button =>
                        (button.Content?.ToString() ?? string.Empty).Contains("ISO 선택", StringComparison.Ordinal));
                    Assert.All(mediaButtons, button =>
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));

                    var imageId = view.GetLogicalDescendants()
                        .OfType<TextBox>()
                        .Single(textBox => AutomationProperties.GetName(textBox) == "Windows 환경 이름");
                    Assert.True(imageId.Focusable);
                    Assert.Equal(980, window.ClientSize.Width);
                    Assert.Equal(760, window.ClientSize.Height);
                    Assert.True(view.FindControl<Border>("StepRail")!.IsVisible);
                    Assert.False(view.FindControl<Border>("CompactStepHeader")!.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task UsesCompactProgressAtMinimumSupportedSize()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));

        await session.Dispatch(
            () =>
            {
                var view = new SetupWizardView();
                var window = new Window
                {
                    Width = 720,
                    Height = 480,
                    Content = view,
                };
                window.Show();
                try
                {
                    Assert.False(view.FindControl<Border>("StepRail")!.IsVisible);
                    Assert.True(view.FindControl<Border>("CompactStepHeader")!.IsVisible);
                    window.SetRenderScaling(2);
                    Assert.Equal(2, window.RenderScaling);
                    Assert.Equal(720, window.ClientSize.Width);
                    Assert.Equal(480, window.ClientSize.Height);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }
}
