using System.Security.Cryptography;
using System.Text;
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
    public async Task ReportsBootstrapStartFailureWithoutLeakingExceptionDetails()
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var launcher = new FakeBootstrapLauncher(throwOnLaunch: true);
        var application = CreateApplication(directory.Path, launcher);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.BootstrapLaunchFailed, exitCode);
    }

    [Fact]
    public async Task ValidProvisioningProbeWritesResultAndBypassesBootstrap()
    {
        using var directory = new TemporaryDirectory();
        using var executableDirectory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var executablePath = Path.Combine(executableDirectory.Path, "linuxcloth-guest-bridge.exe");
        File.WriteAllText(executablePath, "guest bridge", Encoding.ASCII);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
        File.WriteAllText(
            Path.Combine(directory.Path, ProvisioningProbeProcessor.ProbeFileName),
            $$"""{"schemaVersion":1,"nonce":"0123456789abcdef0123456789abcdef","expectedGuestBridgeSha256":"{{hash}}"}""");
        var launcher = new FakeBootstrapLauncher();
        var shutdownRequester = new FakeShutdownRequester();
        var driveProvider = new FakeDriveProvider(directory.Path);
        var application = new GuestBridgeApplication(
            new GuestConfigResolver(driveProvider, NullDiagnosticLog.Instance),
            launcher,
            NullDiagnosticLog.Instance,
            new ProvisioningProbeProcessor(
                driveProvider,
                new FakeExecutableProvider(executablePath),
                new FakeGuestEnvironmentProvider()),
            shutdownRequester);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.Success, exitCode);
        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(1, shutdownRequester.RequestCount);
        Assert.True(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task HashMismatchRecordsNoResultAndContinuesNormalLaunch()
    {
        using var directory = new TemporaryDirectory();
        using var executableDirectory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var executablePath = Path.Combine(executableDirectory.Path, "linuxcloth-guest-bridge.exe");
        File.WriteAllText(executablePath, "guest bridge", Encoding.ASCII);
        File.WriteAllText(
            Path.Combine(directory.Path, ProvisioningProbeProcessor.ProbeFileName),
            $$"""{"schemaVersion":1,"nonce":"0123456789abcdef0123456789abcdef","expectedGuestBridgeSha256":"{{new string('0', 64)}}"}""");
        var launcher = new FakeBootstrapLauncher();
        var shutdownRequester = new FakeShutdownRequester();
        var driveProvider = new FakeDriveProvider(directory.Path);
        var application = new GuestBridgeApplication(
            new GuestConfigResolver(driveProvider, NullDiagnosticLog.Instance),
            launcher,
            NullDiagnosticLog.Instance,
            new ProvisioningProbeProcessor(
                driveProvider,
                new FakeExecutableProvider(executablePath),
                new FakeGuestEnvironmentProvider()),
            shutdownRequester);

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.Success, exitCode);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Equal(0, shutdownRequester.RequestCount);
        Assert.False(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task AmbiguousProvisioningProbesAreDiagnosedAndNormalLaunchContinues()
    {
        using var configDrive = new TemporaryDirectory();
        using var secondProbeDrive = new TemporaryDirectory();
        using var executableDirectory = new TemporaryDirectory();
        ConfigFixture.WriteValid(configDrive.Path);
        var executablePath = Path.Combine(executableDirectory.Path, "linuxcloth-guest-bridge.exe");
        File.WriteAllText(executablePath, "guest bridge", Encoding.ASCII);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
        var probe = $$"""{"schemaVersion":1,"nonce":"0123456789abcdef0123456789abcdef","expectedGuestBridgeSha256":"{{hash}}"}""";
        File.WriteAllText(
            Path.Combine(configDrive.Path, ProvisioningProbeProcessor.ProbeFileName),
            probe);
        File.WriteAllText(
            Path.Combine(secondProbeDrive.Path, ProvisioningProbeProcessor.ProbeFileName),
            probe);
        var driveProvider = new FakeDriveProvider(configDrive.Path, secondProbeDrive.Path);
        var launcher = new FakeBootstrapLauncher();
        var diagnosticLog = new RecordingDiagnosticLog();
        var application = new GuestBridgeApplication(
            new GuestConfigResolver(driveProvider, diagnosticLog),
            launcher,
            diagnosticLog,
            new ProvisioningProbeProcessor(
                driveProvider,
                new FakeExecutableProvider(executablePath),
                new FakeGuestEnvironmentProvider()),
            new FakeShutdownRequester());

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.Success, exitCode);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.Contains(DiagnosticEvent.ProvisioningProbeAmbiguous, diagnosticLog.Events);
    }

    [Fact]
    public async Task ShutdownFailureAfterVerifiedProbeNeverFallsThroughToBootstrap()
    {
        using var directory = new TemporaryDirectory();
        using var executableDirectory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var executablePath = Path.Combine(executableDirectory.Path, "linuxcloth-guest-bridge.exe");
        File.WriteAllText(executablePath, "guest bridge", Encoding.ASCII);
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executablePath)));
        File.WriteAllText(
            Path.Combine(directory.Path, ProvisioningProbeProcessor.ProbeFileName),
            $$"""{"schemaVersion":1,"nonce":"0123456789abcdef0123456789abcdef","expectedGuestBridgeSha256":"{{hash}}"}""");
        var driveProvider = new FakeDriveProvider(directory.Path);
        var launcher = new FakeBootstrapLauncher();
        var application = new GuestBridgeApplication(
            new GuestConfigResolver(driveProvider, NullDiagnosticLog.Instance),
            launcher,
            NullDiagnosticLog.Instance,
            new ProvisioningProbeProcessor(
                driveProvider,
                new FakeExecutableProvider(executablePath),
                new FakeGuestEnvironmentProvider()),
            new FakeShutdownRequester(exitCode: 5));

        var exitCode = await application.RunAsync(CancellationToken.None);

        Assert.Equal(GuestBridgeExitCode.ProvisioningShutdownFailed, exitCode);
        Assert.Equal(0, launcher.LaunchCount);
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
