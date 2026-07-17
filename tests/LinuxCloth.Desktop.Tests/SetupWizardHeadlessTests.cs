using Avalonia;
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
    public async Task RendersSinglePreparationActionWithAccessibleFilePickers()
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
                    Assert.Equal(2, labels.Count(label => label == "파일 선택"));
                    Assert.Contains("Windows 환경 준비하기", labels);
                    Assert.DoesNotContain("뒤로", labels);
                    Assert.DoesNotContain("계속", labels);
                    Assert.DoesNotContain(labels, label => label.Contains("OVMF", StringComparison.Ordinal));
                    Assert.DoesNotContain(labels, label => label.Contains("GuestBridge", StringComparison.Ordinal));

                    var fileButtons = buttons.Where(button =>
                        (button.Content?.ToString() ?? string.Empty) == "파일 선택");
                    Assert.All(fileButtons, button =>
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))));
                    Assert.Contains(
                        buttons,
                        button => AutomationProperties.GetAutomationId(button) == "Setup.Prepare");

                    var imageId = view.GetLogicalDescendants()
                        .OfType<TextBox>()
                        .Single(textBox => AutomationProperties.GetName(textBox) == "Windows 환경 이름");
                    Assert.True(imageId.Focusable);
                    Assert.Equal(980, window.ClientSize.Width);
                    Assert.Equal(760, window.ClientSize.Height);
                    Assert.Equal(new Thickness(20, 16), view.FindControl<Grid>("ContentFrame")!.Margin);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task KeepsAllContentScrollableAtMinimumSizeAndTwoHundredPercentScaling()
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
                    Assert.Equal(new Thickness(12, 10), view.FindControl<Grid>("ContentFrame")!.Margin);
                    Assert.NotNull(view.GetLogicalDescendants().OfType<ScrollViewer>().SingleOrDefault());
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
