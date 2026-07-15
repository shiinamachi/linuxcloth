using System.ComponentModel;

namespace LinuxCloth.GuestBridge;

internal sealed class GuestBridgeApplication
{
    private readonly GuestConfigResolver _configResolver;
    private readonly IBootstrapLauncher _bootstrapLauncher;
    private readonly IDiagnosticLog _diagnosticLog;
    private readonly IProvisioningProbeProcessor _provisioningProbeProcessor;
    private readonly IShutdownRequester _shutdownRequester;

    public GuestBridgeApplication(
        GuestConfigResolver configResolver,
        IBootstrapLauncher bootstrapLauncher,
        IDiagnosticLog diagnosticLog)
        : this(
            configResolver,
            bootstrapLauncher,
            diagnosticLog,
            NullProvisioningProbeProcessor.Instance,
            NullShutdownRequester.Instance)
    {
    }

    public GuestBridgeApplication(
        GuestConfigResolver configResolver,
        IBootstrapLauncher bootstrapLauncher,
        IDiagnosticLog diagnosticLog,
        IProvisioningProbeProcessor provisioningProbeProcessor,
        IShutdownRequester shutdownRequester)
    {
        _configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
        _bootstrapLauncher = bootstrapLauncher ?? throw new ArgumentNullException(nameof(bootstrapLauncher));
        _diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
        _provisioningProbeProcessor = provisioningProbeProcessor ??
                                      throw new ArgumentNullException(nameof(provisioningProbeProcessor));
        _shutdownRequester = shutdownRequester ?? throw new ArgumentNullException(nameof(shutdownRequester));
    }

    public async Task<GuestBridgeExitCode> RunAsync(CancellationToken cancellationToken)
    {
        var provisioningExitCode = await TryProvisionAsync(cancellationToken).ConfigureAwait(false);
        if (provisioningExitCode is not null)
        {
            return provisioningExitCode.Value;
        }

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

    private async Task<GuestBridgeExitCode?> TryProvisionAsync(CancellationToken cancellationToken)
    {
        ProvisioningProbeOutcome outcome;
        try
        {
            outcome = await _provisioningProbeProcessor.ProcessAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticLog.Write(DiagnosticEvent.Cancelled);
            return GuestBridgeExitCode.Cancelled;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            _diagnosticLog.Write(DiagnosticEvent.ProvisioningProbeInvalid);
            return null;
        }

        switch (outcome.Status)
        {
            case ProvisioningProbeStatus.NotFound:
                return null;
            case ProvisioningProbeStatus.Invalid:
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningProbeInvalid);
                return null;
            case ProvisioningProbeStatus.Ambiguous:
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningProbeAmbiguous);
                return null;
            case ProvisioningProbeStatus.GuestBridgeHashMismatch:
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningHashMismatch);
                return null;
            case ProvisioningProbeStatus.ResultWriteFailed:
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningResultWriteFailed);
                return null;
            case ProvisioningProbeStatus.Success:
                break;
            default:
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningProbeInvalid);
                return null;
        }

        _diagnosticLog.Write(DiagnosticEvent.ProvisioningVerified);
        try
        {
            var shutdownExitCode = await _shutdownRequester
                .RequestShutdownAsync(cancellationToken)
                .ConfigureAwait(false);
            if (shutdownExitCode == 0)
            {
                _diagnosticLog.Write(DiagnosticEvent.ProvisioningShutdownRequested);
                return GuestBridgeExitCode.Success;
            }

            _diagnosticLog.Write(DiagnosticEvent.ProvisioningShutdownFailed);
            return GuestBridgeExitCode.ProvisioningShutdownFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticLog.Write(DiagnosticEvent.Cancelled);
            return GuestBridgeExitCode.Cancelled;
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            IOException or
            InvalidOperationException)
        {
            _diagnosticLog.Write(DiagnosticEvent.ProvisioningShutdownFailed);
            return GuestBridgeExitCode.ProvisioningShutdownFailed;
        }
    }
}
