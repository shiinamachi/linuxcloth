namespace LinuxCloth.GuestBridge.Tests;

public sealed class WindowsShutdownRequesterTests
{
    [Fact]
    public async Task UsesFixedShutdownExecutableAndArgumentBoundaries()
    {
        var processRunner = new CapturingProcessRunner();
        var requester = new WindowsShutdownRequester(processRunner);

        var exitCode = await requester.RequestShutdownAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        var startInfo = Assert.IsType<System.Diagnostics.ProcessStartInfo>(processRunner.StartInfo);
        Assert.EndsWith("shutdown.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/s", "/t", "0"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }
}
