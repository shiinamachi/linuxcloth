namespace LinuxCloth.GuestBridge;

internal enum GuestBridgeExitCode
{
    Success = 0,
    UnexpectedFailure = 1,
    UnsupportedPlatform = 2,
    AlreadyRunning = 3,
    ConfigurationNotFound = 10,
    ConfigurationInvalid = 11,
    ConfigurationAmbiguous = 12,
    BootstrapLaunchFailed = 20,
    BootstrapFailed = 21,
    ProvisioningShutdownFailed = 30,
    Cancelled = 130,
}
