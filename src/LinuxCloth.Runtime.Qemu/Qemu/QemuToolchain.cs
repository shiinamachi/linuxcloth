namespace LinuxCloth.Runtime.Qemu.Qemu;

public sealed record QemuToolchain(
    string QemuSystem,
    string QemuImg,
    string Swtpm,
    string Passt,
    string RemoteViewer);

