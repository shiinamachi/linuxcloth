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
                    var automationIds = buttons
                        .Select(AutomationProperties.GetAutomationId)
                        .Where(static id => !string.IsNullOrWhiteSpace(id))
                        .ToArray();
                    var accessibleNames = buttons
                        .Select(AutomationProperties.GetName)
                        .Where(static name => !string.IsNullOrWhiteSpace(name))
                        .ToArray();

                    Assert.Contains("Setup.Windows.Select", automationIds);
                    Assert.Contains("Setup.Drivers.Select", automationIds);
                    Assert.Contains("Setup.Prepare", automationIds);
                    Assert.Contains("Setup.ViewInstaller", automationIds);
                    Assert.Contains("Windows 11 설치 파일 선택", accessibleNames);
                    Assert.Contains("Windows 장치 드라이버 파일 선택", accessibleNames);
                    Assert.DoesNotContain(
                        automationIds,
                        static id => id is not null && id.Contains("Back", StringComparison.Ordinal));
                    Assert.DoesNotContain(
                        accessibleNames,
                        static name => name is not null && name.Contains("OVMF", StringComparison.Ordinal));
                    Assert.DoesNotContain(
                        accessibleNames,
                        static name => name is not null && name.Contains("GuestBridge", StringComparison.Ordinal));

                    var windowsSelect = buttons.Single(button =>
                        AutomationProperties.GetAutomationId(button) == "Setup.Windows.Select");
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(windowsSelect)));

                    var prepare = buttons.Single(button =>
                        AutomationProperties.GetAutomationId(button) == "Setup.Prepare");
                    Assert.Equal("Windows 환경 준비하기", prepare.Content?.ToString());

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
