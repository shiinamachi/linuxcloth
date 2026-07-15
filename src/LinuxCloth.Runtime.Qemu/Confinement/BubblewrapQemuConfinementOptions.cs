namespace LinuxCloth.Runtime.Qemu.Confinement;

public sealed record BubblewrapQemuConfinementOptions(
    string BubblewrapExecutable,
    string SessionDirectory,
    string BaseImagePath,
    string OvmfCodePath);
