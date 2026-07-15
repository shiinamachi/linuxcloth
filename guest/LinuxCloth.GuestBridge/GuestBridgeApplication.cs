using System.ComponentModel;

namespace LinuxCloth.GuestBridge;

internal sealed class GuestBridgeApplication
{
    private readonly GuestConfigResolver _configResolver;
    private readonly IBootstrapLauncher _bootstrapLauncher;
    private readonly IDiagnosticLog _diagnosticLog;

    public GuestBridgeApplication(
        GuestConfigResolver configResolver,
        IBootstrapLauncher bootstrapLauncher,
        IDiagnosticLog diagnosticLog)
    {
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _bootstrapLauncher = bootstrapLauncher ?? throw new ArgumentNullException(nameof(bootstrapLauncher));
        _diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
    }

    public async Task<GuestBridgeExitCode> RunAsync(CancellationToken cancellationToken)
    {
        ConfigResolution resolution;
        try
        {
            resolution = _configResolver.Resolve();
        }
        catch (IOException)
        {
            _diagnosticLog.Write(DiagnosticEvent.ConfigurationRejected);
            return GuestBridgeExitCode.ConfigurationInvalid;
        }
        catch (UnauthorizedAccessException)
        {
            _diagnosticLog.Write(DiagnosticEvent.ConfigurationRejected);
            return GuestBridgeExitCode.ConfigurationInvalid;
        }

        switch (resolution.Status)
        {
            case ConfigResolutionStatus.NotFound:
                _diagnosticLog.Write(DiagnosticEvent.ConfigurationNotFound);
                return GuestBridgeExitCode.ConfigurationNotFound;
            case ConfigResolutionStatus.Invalid:
                _diagnosticLog.Write(DiagnosticEvent.ConfigurationRejected);
                return GuestBridgeExitCode.ConfigurationInvalid;
            case ConfigResolutionStatus.Ambiguous:
                _diagnosticLog.Write(DiagnosticEvent.ConfigurationAmbiguous);
                return GuestBridgeExitCode.ConfigurationAmbiguous;
            case ConfigResolutionStatus.Success:
                break;
            default:
                _diagnosticLog.Write(DiagnosticEvent.UnexpectedFailure);
                return GuestBridgeExitCode.UnexpectedFailure;
        }

        var manifest = resolution.Manifest
            ?? throw new InvalidOperationException("A successful resolution must contain a manifest.");

        try
        {
            _diagnosticLog.Write(DiagnosticEvent.BootstrapStarted);
            var processExitCode = await _bootstrapLauncher
                .LaunchAsync(manifest.ServiceIds, cancellationToken)
                .ConfigureAwait(false);

            if (processExitCode == 0)
            {
                _diagnosticLog.Write(DiagnosticEvent.BootstrapCompleted);
                return GuestBridgeExitCode.Success;
            }

            _diagnosticLog.Write(DiagnosticEvent.BootstrapFailed);
            return GuestBridgeExitCode.BootstrapFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticLog.Write(DiagnosticEvent.Cancelled);
            return GuestBridgeExitCode.Cancelled;
        }
        catch (Win32Exception)
        {
            _diagnosticLog.Write(DiagnosticEvent.BootstrapLaunchFailed);
            return GuestBridgeExitCode.BootstrapLaunchFailed;
        }
        catch (IOException)
        {
            _diagnosticLog.Write(DiagnosticEvent.BootstrapLaunchFailed);
            return GuestBridgeExitCode.BootstrapLaunchFailed;
        }
        catch (InvalidOperationException)
        {
            _diagnosticLog.Write(DiagnosticEvent.BootstrapLaunchFailed);
            return GuestBridgeExitCode.BootstrapLaunchFailed;
        }
    }
}
