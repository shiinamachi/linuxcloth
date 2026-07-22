using LinuxCloth.Runtime.Qemu.Qemu;

namespace LinuxCloth.Runtime.Qemu.Doctor;

public static class QemuDoctorCheckCodes
{
    public const string Platform = "platform";
    public const string Kvm = "kvm";
    public const string QemuSystem = "qemu-system-x86_64";
    public const string QemuImg = "qemu-img";
    public const string Swtpm = "swtpm";
    public const string RemoteViewer = "remote-viewer";
    public const string Passt = "passt";
    public const string Bubblewrap = "bwrap";
    public const string Firmware = "firmware";
    public const string RuntimeDirectory = "xdg-runtime";
    public const string WimlibImagex = "wimlib-imagex";
    public const string SevenZip = "7z";
    public const string Xorriso = "xorriso";
}

public sealed class QemuDoctorOptions
{
    public const int MaximumSessionSocketPathBytes = 100;

    public string KvmDevicePath { get; init; } = "/dev/kvm";

    public string FirmwareDescriptorDirectory { get; init; } =
        FirmwareDescriptorResolver.DefaultDescriptorDirectory;

    public string? XdgRuntimeDirectory { get; init; } =
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

    public int MaximumUnixSocketPathBytes { get; init; } = MaximumSessionSocketPathBytes;
}

public sealed record QemuLaunchPrerequisites(
    QemuToolchain Toolchain,
    FirmwarePair Firmware,
    string Bubblewrap,
    string RuntimeDirectory);

public sealed record ImageBuildPrerequisites(
    string WimlibImagex,
    string SevenZip,
    string Xorriso);

public sealed record QemuDoctorResult(
    DoctorReport Report,
    QemuLaunchPrerequisites? LaunchPrerequisites,
    ImageBuildPrerequisites? ImageBuildPrerequisites,
    QemuLaunchPrerequisites? OfflineLaunchPrerequisites)
{
    public bool CanLaunch => LaunchPrerequisites is not null;

    public bool CanLaunchOffline => OfflineLaunchPrerequisites is not null;

    public bool CanBuildImage => ImageBuildPrerequisites is not null;
}

public sealed record HostProbeResult(bool IsAvailable, string Detail);
