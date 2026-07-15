using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class RecoveryProcessIdentityControllerTests
{
    [Fact]
    public async Task NeverSignalsProcessWhenFullIdentityDoesNotMatch()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/sleep"))
        {
            return;
        }

        await using var process = await new SystemProcessLauncher().StartAsync(
            new ProcessSpec("/usr/bin/sleep", ["30"]));
        var mismatched = process.Identity with { StartTicks = process.Identity.StartTicks + 1 };
        var controller = new LinuxProcessIdentityController();

        var terminate = await controller.SendTerminateAsync(mismatched);
        var kill = await controller.SendKillAsync(mismatched);

        Assert.Equal(RecoverySignalResult.IdentityMismatch, terminate);
        Assert.Equal(RecoverySignalResult.IdentityMismatch, kill);
        Assert.False(process.HasExited);
    }

    [Fact]
    public async Task SignalsOnlyTheVerifiedPidfdTarget()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/sleep"))
        {
            return;
        }

        await using var process = await new SystemProcessLauncher().StartAsync(
            new ProcessSpec("/usr/bin/sleep", ["30"]));
        var controller = new LinuxProcessIdentityController();

        var signal = await controller.SendTerminateAsync(process.Identity);
        var exitCode = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var status = await controller.InspectAsync(process.Identity);

        Assert.Equal(RecoverySignalResult.Sent, signal);
        Assert.NotEqual(0, exitCode);
        Assert.Equal(RecoveryProcessStatus.Stopped, status);
    }
}
