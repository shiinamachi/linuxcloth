using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public enum WindowsImageBuildPhase
{
    Preparing,
    Prepared,
    InstallerRunning,
    ReadyToVerify,
    VerificationRunning,
    ReadyToFinalize,
}

public sealed record WindowsImageBuildToolchain(
    string QemuSystem,
    string QemuImg,
    string Swtpm,
    string RemoteViewer,
    string SevenZip,
    string Xorriso,
    string Bubblewrap);

public sealed record WindowsInstallationSelection(
    int ImageIndex,
    string EditionId,
    string DisplayName);

public sealed record WindowsImageBuildRequest(
    ImageId ImageId,
    string WindowsIsoPath,
    string VirtioWinIsoPath,
    string GuestBridgeExecutablePath,
    string OvmfCodePath,
    string OvmfVariablesTemplatePath,
    WindowsImageBuildToolchain Toolchain,
    int DiskSizeGiB = 96,
    int CpuCount = 4,
    int MemoryMiB = 6144,
    WindowsInstallationSelection? Installation = null);

public sealed record ImageBuildFileFingerprint(
    string Path,
    string Sha256,
    long Length,
    long LastWriteUtcTicks);

public sealed record ValidatedInstallationMedia(
    ImageBuildFileFingerprint WindowsIso,
    ImageBuildFileFingerprint VirtioWinIso);

public sealed record VerifiedGuestEnvironment(
    string GuestBridgeVersion,
    string WindowsArchitecture,
    int WindowsBuild,
    string WindowsEditionId,
    string WindowsDisplayVersion,
    DateTimeOffset VerifiedAt);

public sealed record WindowsImageBuildState(
    int SchemaVersion,
    ImageId ImageId,
    Guid MachineId,
    WindowsImageBuildPhase Phase,
    ImageBuildFileFingerprint WindowsIso,
    ImageBuildFileFingerprint VirtioWinIso,
    ImageBuildFileFingerprint GuestBridgeExecutable,
    ImageBuildFileFingerprint OvmfCode,
    ImageBuildFileFingerprint OvmfVariablesSource,
    WindowsImageBuildToolchain Toolchain,
    WindowsInstallationSelection Installation,
    int DiskSizeGiB,
    int CpuCount,
    int MemoryMiB,
    string? ActiveHostBootId,
    string? PendingProcessName,
    IReadOnlyDictionary<string, ProcessIdentity> ActiveProcesses,
    string? VerificationNonce,
    VerifiedGuestEnvironment? VerifiedGuestEnvironment,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 3;
}

public sealed record WindowsImageBuildWorkspace(
    ImageRegistrationStaging Staging,
    WindowsImageBuildState State,
    string RuntimeDirectory)
{
    public string SocketsDirectory => Path.Combine(RuntimeDirectory, "sockets");

    public string SwtpmSocketPath => Path.Combine(SocketsDirectory, "t.sock");

    public string SpiceSocketPath => Path.Combine(SocketsDirectory, "s.sock");

    public string QmpSocketPath => Path.Combine(SocketsDirectory, "q.sock");

    public string ProvisioningSourceDirectory => Path.Combine(RuntimeDirectory, "provisioning");

    public string ProvisioningIsoPath => Path.Combine(RuntimeDirectory, "linuxcloth-provisioning.iso");

    public string VerificationDirectory => Path.Combine(RuntimeDirectory, "verification");
}

public static class WindowsImageBuildProcessNames
{
    public const string Swtpm = "swtpm";
    public const string Qemu = "qemu";
    public const string Viewer = "viewer";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Swtpm, Qemu, Viewer],
        StringComparer.Ordinal);
}

public static class GuestBridgeProvisioningContract
{
    public const int SchemaVersion = 1;
    public const string ExecutableFileName = "linuxcloth-guest-bridge.exe";
    public const string AutounattendFileName = "Autounattend.xml";
    public const string InstallScriptFileName = "Install-LinuxCloth.ps1";
    public const string InstallCommandFileName = "Install-LinuxCloth.cmd";
    public const string ReadmeFileName = "README.txt";
    public const string ProbeFileName = "linuxcloth-provision-probe.json";
    public const string ResultFileName = "linuxcloth-provision-result.json";
}

public sealed class WindowsImageBuildException : Exception
{
    public WindowsImageBuildException(string message, ImageRegistrationStaging? staging = null)
        : base(message)
    {
        Staging = staging;
    }

    public WindowsImageBuildException(
        string message,
        Exception innerException,
        ImageRegistrationStaging? staging = null)
        : base(message, innerException)
    {
        Staging = staging;
    }

    public ImageRegistrationStaging? Staging { get; }
}

public sealed class WindowsImageBuildCanceledException : OperationCanceledException
{
    public WindowsImageBuildCanceledException(
        string message,
        OperationCanceledException innerException,
        ImageRegistrationStaging staging)
        : base(message, innerException, innerException.CancellationToken)
    {
        Staging = staging;
    }

    public ImageRegistrationStaging Staging { get; }
}
