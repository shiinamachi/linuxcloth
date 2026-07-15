namespace LinuxCloth.GuestBridge;

internal static class Program
{
    public static async Task<int> Main()
    {
        var diagnosticLog = CreateDiagnosticLog();
        diagnosticLog.Write(DiagnosticEvent.Started);

        if (!OperatingSystem.IsWindows())
        {
            diagnosticLog.Write(DiagnosticEvent.UnsupportedPlatform);
            return (int)GuestBridgeExitCode.UnsupportedPlatform;
        }

        using var singleInstance = SingleInstanceGuard.TryAcquire();
        if (singleInstance is null)
        {
            diagnosticLog.Write(DiagnosticEvent.AlreadyRunning);
            return (int)GuestBridgeExitCode.AlreadyRunning;
        }

        try
        {
            var driveProvider = new SystemConfigDriveProvider();
            var processRunner = new SystemProcessRunner();
            using var bootstrapHttpClient = HttpBootstrapArtifactDownloader.CreateSecureHttpClient();
            var configResolver = new GuestConfigResolver(driveProvider, diagnosticLog);
            var bootstrapLauncher = new PinnedBootstrapLauncher(
                new HttpBootstrapArtifactDownloader(bootstrapHttpClient),
                new WindowsAuthenticodeVerifier(),
                processRunner,
                PrivateTemporaryDirectoryFactory.Instance);
            var guestReadyReporter = new VirtioSerialGuestReadyReporter();
            var provisioningProbeProcessor = new ProvisioningProbeProcessor(
                driveProvider,
                new SystemGuestBridgeExecutableProvider(),
                new SystemGuestEnvironmentProvider());
            var shutdownRequester = new WindowsShutdownRequester(processRunner);
            var application = new GuestBridgeApplication(
                configResolver,
                bootstrapLauncher,
                diagnosticLog,
                provisioningProbeProcessor,
                shutdownRequester,
                guestReadyReporter);
            var exitCode = await application.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return (int)exitCode;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            diagnosticLog.Write(DiagnosticEvent.UnexpectedFailure);
            return (int)GuestBridgeExitCode.UnexpectedFailure;
        }
    }

    private static BoundedFileDiagnosticLog CreateDiagnosticLog()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrEmpty(localApplicationData)
            ? Path.GetTempPath()
            : localApplicationData;
        var path = Path.Combine(root, "linuxcloth", "GuestBridge", "diagnostics.log");
        return new BoundedFileDiagnosticLog(path);
    }
}
