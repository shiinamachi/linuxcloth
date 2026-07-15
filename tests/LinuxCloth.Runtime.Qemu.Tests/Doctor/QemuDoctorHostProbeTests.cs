using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Runtime.Qemu.Tests.Doctor;

public sealed class QemuDoctorHostProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lch-{Guid.NewGuid():N}"[..12]);

    [Fact]
    public void ProbesReadWriteDeviceUsingInjectedPath()
    {
        Directory.CreateDirectory(_root);
        var devicePath = Path.Combine(_root, "kvm");
        File.WriteAllBytes(devicePath, [0]);

        var result = new SystemQemuDoctorHostProbe().ProbeReadWriteDevice(devicePath);

        Assert.True(result.IsAvailable);
        Assert.Contains(devicePath, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbesUnixSocketAndRemovesTemporaryArtifacts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(
            _root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = new SystemQemuDoctorHostProbe().ProbeUnixSocketDirectory(_root);

        Assert.True(result.IsAvailable);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void ReportsMissingRuntimeDirectoryWithoutCreatingIt()
    {
        var missing = Path.Combine(_root, "missing");

        var result = new SystemQemuDoctorHostProbe().ProbeUnixSocketDirectory(missing);

        Assert.False(result.IsAvailable);
        Assert.False(Directory.Exists(missing));
        Assert.Contains("does not exist", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRuntimeDirectoryVisibleToOtherUsers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(
            _root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var result = new SystemQemuDoctorHostProbe().ProbeUnixSocketDirectory(_root);

        Assert.False(result.IsAvailable);
        Assert.Contains("0700", result.Detail, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
