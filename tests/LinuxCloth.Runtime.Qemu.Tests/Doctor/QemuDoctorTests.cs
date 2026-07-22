using System.Runtime.InteropServices;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Runtime.Qemu.Tests.Doctor;

public sealed class QemuDoctorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lcd-{Guid.NewGuid():N}"[..12]);

    [Fact]
    public async Task ReturnsLaunchAndImageBuildPrerequisitesWhenEveryCapabilityPasses()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var binaryDirectory = CreateExecutables(
            QemuDoctorCheckCodes.QemuSystem,
            QemuDoctorCheckCodes.QemuImg,
            QemuDoctorCheckCodes.Swtpm,
            QemuDoctorCheckCodes.RemoteViewer,
            QemuDoctorCheckCodes.Passt,
            QemuDoctorCheckCodes.Bubblewrap,
            QemuDoctorCheckCodes.WimlibImagex,
            QemuDoctorCheckCodes.SevenZip,
            QemuDoctorCheckCodes.Xorriso);
        var xdgRuntime = CreateDirectory("runtime");
        var kvmPath = CreateFile("devices/kvm");
        var host = new FakeHostProbe();
        var doctor = CreateDoctor(binaryDirectory, firmware, xdgRuntime, kvmPath, host);

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.True(result.Report.CanLaunch);
        Assert.True(result.CanLaunch);
        Assert.True(result.CanLaunchOffline);
        Assert.True(result.CanBuildImage);
        var launch = Assert.IsType<QemuLaunchPrerequisites>(result.LaunchPrerequisites);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.QemuSystem), launch.Toolchain.QemuSystem);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.QemuImg), launch.Toolchain.QemuImg);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.Swtpm), launch.Toolchain.Swtpm);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.Passt), launch.Toolchain.Passt);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.RemoteViewer), launch.Toolchain.RemoteViewer);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.Bubblewrap), launch.Bubblewrap);
        Assert.Equal(Path.Combine(xdgRuntime, "linuxcloth"), launch.RuntimeDirectory);
        Assert.Equal(firmware.ExecutablePath, launch.Firmware.Executable.Path);
        Assert.Equal(xdgRuntime, host.LastRuntimeProbePath);

        var image = Assert.IsType<ImageBuildPrerequisites>(result.ImageBuildPrerequisites);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.WimlibImagex), image.WimlibImagex);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.SevenZip), image.SevenZip);
        Assert.Equal(Path.Combine(binaryDirectory, QemuDoctorCheckCodes.Xorriso), image.Xorriso);
    }

    [Fact]
    public async Task MissingPasstStillReturnsAnOfflineLaunchToolchain()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var binaryDirectory = CreateExecutables(
            QemuDoctorCheckCodes.QemuSystem,
            QemuDoctorCheckCodes.QemuImg,
            QemuDoctorCheckCodes.Swtpm,
            QemuDoctorCheckCodes.RemoteViewer,
            QemuDoctorCheckCodes.Bubblewrap);
        var doctor = CreateDoctor(
            binaryDirectory,
            firmware,
            CreateDirectory("runtime"),
            CreateFile("devices/kvm"),
            new FakeHostProbe());

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.False(result.CanLaunch);
        Assert.True(result.CanLaunchOffline);
        Assert.Null(result.LaunchPrerequisites);
        var offline = Assert.IsType<QemuLaunchPrerequisites>(result.OfflineLaunchPrerequisites);
        Assert.Null(offline.Toolchain.Passt);
    }

    [Fact]
    public async Task MissingRequiredBubblewrapBlocksLaunchButOptionalImageToolsDoNotAffectRequiredChecks()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var binaryDirectory = CreateExecutables(
            QemuDoctorCheckCodes.QemuSystem,
            QemuDoctorCheckCodes.QemuImg,
            QemuDoctorCheckCodes.Swtpm,
            QemuDoctorCheckCodes.RemoteViewer,
            QemuDoctorCheckCodes.Passt);
        var doctor = CreateDoctor(
            binaryDirectory,
            firmware,
            CreateDirectory("runtime"),
            CreateFile("devices/kvm"),
            new FakeHostProbe());

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.False(result.CanLaunch);
        Assert.Null(result.LaunchPrerequisites);
        Assert.False(result.CanBuildImage);
        var bubblewrap = Assert.Single(
            result.Report.Checks,
            check => check.Name == QemuDoctorCheckCodes.Bubblewrap);
        Assert.True(bubblewrap.IsRequired);
        Assert.False(bubblewrap.IsAvailable);
        var wimlib = Assert.Single(
            result.Report.Checks,
            check => check.Name == QemuDoctorCheckCodes.WimlibImagex);
        Assert.False(wimlib.IsRequired);
        Assert.False(wimlib.IsAvailable);
    }

    [Fact]
    public async Task MissingOnlyImageToolsDoesNotBlockDisposableVmLaunch()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var binaryDirectory = CreateLaunchExecutables();
        var doctor = CreateDoctor(
            binaryDirectory,
            firmware,
            CreateDirectory("runtime"),
            CreateFile("devices/kvm"),
            new FakeHostProbe());

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.True(result.Report.CanLaunch);
        Assert.True(result.CanLaunch);
        Assert.False(result.CanBuildImage);
        Assert.Null(result.ImageBuildPrerequisites);
    }

    [Fact]
    public async Task RejectsUnsupportedPlatformAndKvmAccessWithoutReturningToolchain()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var host = new FakeHostProbe
        {
            IsLinux = false,
            ProcessArchitecture = Architecture.Arm64,
            KvmResult = new HostProbeResult(false, "Injected KVM denial."),
        };
        var doctor = CreateDoctor(
            CreateLaunchExecutables(),
            firmware,
            CreateDirectory("runtime"),
            CreateFile("devices/kvm"),
            host);

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.False(result.CanLaunch);
        Assert.Null(result.LaunchPrerequisites);
        Assert.False(result.Report.Checks.Single(check => check.Name == QemuDoctorCheckCodes.Platform).IsAvailable);
        Assert.Equal(
            "Injected KVM denial.",
            result.Report.Checks.Single(check => check.Name == QemuDoctorCheckCodes.Kvm).Detail);
    }

    [Fact]
    public async Task RejectsLongRuntimeSocketPathBeforeTouchingFilesystem()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var host = new FakeHostProbe();
        var longRuntime = Path.Combine(_root, new string('x', 80));
        var doctor = CreateDoctor(
            CreateLaunchExecutables(),
            firmware,
            longRuntime,
            CreateFile("devices/kvm"),
            host);

        var result = await doctor.InspectDetailedAsync(CancellationToken.None);

        Assert.False(result.CanLaunch);
        Assert.Null(host.LastRuntimeProbePath);
        var check = result.Report.Checks.Single(item => item.Name == QemuDoctorCheckCodes.RuntimeDirectory);
        Assert.False(check.IsAvailable);
        Assert.Contains("safety limit", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyInspectApiReturnsTheStructuredReport()
    {
        using var firmware = new FirmwareDescriptorFixture();
        firmware.WriteDescriptor();
        var doctor = CreateDoctor(
            CreateLaunchExecutables(),
            firmware,
            CreateDirectory("runtime"),
            CreateFile("devices/kvm"),
            new FakeHostProbe());

        var report = await doctor.InspectAsync(CancellationToken.None);

        Assert.True(report.CanLaunch);
        Assert.Equal(13, report.Checks.Count);
        Assert.Equal(
            Path.Combine(_root, "bin", QemuDoctorCheckCodes.QemuSystem),
            report.FindPath(QemuDoctorCheckCodes.QemuSystem));
    }

    private static QemuDoctor CreateDoctor(
        string binaryDirectory,
        FirmwareDescriptorFixture firmware,
        string xdgRuntime,
        string kvmPath,
        IQemuDoctorHostProbe host) =>
        new(
            new ExecutableLocator(binaryDirectory),
            new QemuDoctorOptions
            {
                KvmDevicePath = kvmPath,
                FirmwareDescriptorDirectory = firmware.DescriptorDirectory,
                XdgRuntimeDirectory = xdgRuntime,
            },
            host);

    private string CreateLaunchExecutables() => CreateExecutables(
        QemuDoctorCheckCodes.QemuSystem,
        QemuDoctorCheckCodes.QemuImg,
        QemuDoctorCheckCodes.Swtpm,
        QemuDoctorCheckCodes.RemoteViewer,
        QemuDoctorCheckCodes.Passt,
        QemuDoctorCheckCodes.Bubblewrap);

    private string CreateExecutables(params string[] names)
    {
        var directory = CreateDirectory("bin");
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, "test executable");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
        }

        return directory;
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private string CreateDirectory(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class FakeHostProbe : IQemuDoctorHostProbe
    {
        public bool IsLinux { get; init; } = true;

        public Architecture ProcessArchitecture { get; init; } = Architecture.X64;

        public string PlatformDescription { get; init; } = "test Linux";

        public HostProbeResult KvmResult { get; init; } = new(true, "Injected KVM success.");

        public HostProbeResult RuntimeResult { get; init; } = new(true, "Injected socket success.");

        public string? LastRuntimeProbePath { get; private set; }

        public HostProbeResult ProbeReadWriteDevice(string path) => KvmResult;

        public HostProbeResult ProbeUnixSocketDirectory(string path)
        {
            LastRuntimeProbePath = path;
            return RuntimeResult;
        }
    }
}
