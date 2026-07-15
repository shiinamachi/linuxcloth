using LinuxCloth.Core;

namespace LinuxCloth.Runtime.Qemu.Qemu;

public sealed record QemuLaunchConfiguration(
    QemuToolchain Toolchain,
    LaunchRequest Request,
    Guid SessionId,
    Guid MachineId,
    string SessionDirectory,
    string OverlayPath,
    string OvmfCodePath,
    string OvmfVariablesPath,
    string SwtpmSocketPath,
    string QmpSocketPath,
    string SpiceSocketPath,
    string ConfigDirectory,
    string? PasstSocketPath);

