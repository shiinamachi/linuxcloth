using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class PowerShellBootstrapLauncherTests
{
    [Fact]
    public async Task UsesFixedArgumentsAndEnvironmentWithoutInterpolatingConfigValues()
    {
        var processRunner = new CapturingProcessRunner();
        var launcher = new PowerShellBootstrapLauncher(processRunner);
        var serviceIds = new[]
        {
            ServiceId.Parse("WooriBank"),
            ServiceId.Parse("KB-Bank"),
        };

        var exitCode = await launcher.LaunchAsync(serviceIds, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var startInfo = Assert.IsType<System.Diagnostics.ProcessStartInfo>(processRunner.StartInfo);
        Assert.Equal("powershell.exe", Path.GetFileName(startInfo.FileName));
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            [
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                PowerShellBootstrapLauncher.FixedBootstrapCommand,
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain("WooriBank", PowerShellBootstrapLauncher.FixedBootstrapCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("KB-Bank", PowerShellBootstrapLauncher.FixedBootstrapCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(
            PowerShellBootstrapLauncher.OfficialScriptUrl,
            PowerShellBootstrapLauncher.FixedBootstrapCommand,
            StringComparison.Ordinal);
        Assert.Equal(
            "WooriBank KB-Bank",
            startInfo.Environment[PowerShellBootstrapLauncher.SiteIdsEnvironmentVariable]);
        Assert.Equal(
            PowerShellBootstrapLauncher.OfficialScriptUrl,
            startInfo.Environment[PowerShellBootstrapLauncher.ScriptUrlEnvironmentVariable]);
    }

    [Fact]
    public async Task RejectsDuplicateServiceIdentifiersBeforeStartingPowerShell()
    {
        var processRunner = new CapturingProcessRunner();
        var launcher = new PowerShellBootstrapLauncher(processRunner);
        var serviceId = ServiceId.Parse("WooriBank");

        await Assert.ThrowsAsync<ArgumentException>(
            () => launcher.LaunchAsync([serviceId, serviceId], CancellationToken.None));

        Assert.Null(processRunner.StartInfo);
    }
}
