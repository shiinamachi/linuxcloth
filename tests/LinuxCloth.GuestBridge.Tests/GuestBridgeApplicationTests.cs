using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class GuestBridgeApplicationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 21)]
    [InlineData(37, 21)]
    public async Task MapsBootstrapSuccessAndFailureToStableExitCodes(
        int processExitCode,
        int expectedExitCode)
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(
            directory.Path,
            [ServiceId.Parse("WooriBank"), ServiceId.Parse("KB")]);
        var launcher = new FakeBootstrapLauncher(processExitCode);
        var application = CreateApplication(directory.Path, launcher);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal((GuestBridgeExitCode)expectedExitCode, exitCode);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(
            [ServiceId.Parse("WooriBank"), ServiceId.Parse("KB")],
            launcher.ServiceIds);
    }

    [Fact]
    public async Task DoesNotLaunchWhenConfigsAreAmbiguous()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        ConfigFixture.WriteValid(first.Path);
        ConfigFixture.WriteValid(second.Path);
        var launcher = new FakeBootstrapLauncher();
        var resolver = new GuestConfigResolver(
            new FakeDriveProvider(first.Path, second.Path),
            NullDiagnosticLog.Instance);
        var application = new GuestBridgeApplication(
            resolver,
            launcher,
            NullDiagnosticLog.Instance);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.ConfigurationAmbiguous, exitCode);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public async Task ReportsPowerShellStartFailureWithoutLeakingExceptionDetails()
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var launcher = new FakeBootstrapLauncher(throwOnLaunch: true);
        var application = CreateApplication(directory.Path, launcher);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.BootstrapLaunchFailed, exitCode);
    }

    private static GuestBridgeApplication CreateApplication(
        string root,
        IBootstrapLauncher launcher)
    {
        var resolver = new GuestConfigResolver(
            new FakeDriveProvider(root),
            NullDiagnosticLog.Instance);
        return new GuestBridgeApplication(resolver, launcher, NullDiagnosticLog.Instance);
    }
}
