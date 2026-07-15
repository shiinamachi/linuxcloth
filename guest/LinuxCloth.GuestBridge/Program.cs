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
            var configResolver = new GuestConfigResolver(
                new SystemConfigDriveProvider(),
                diagnosticLog);
            var bootstrapLauncher = new PowerShellBootstrapLauncher(new SystemProcessRunner());
            var application = new GuestBridgeApplication(
                configResolver,
                bootstrapLauncher,
                diagnosticLog);
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
