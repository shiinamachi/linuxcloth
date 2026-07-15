using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"linuxcloth-test-{Guid.NewGuid():N}");

    [Fact]
    public void FindsExecutableInConfiguredPath()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_temporaryDirectory);
        var executable = Path.Combine(_temporaryDirectory, "qemu-test");
        File.WriteAllText(executable, "#!/bin/sh\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = new ExecutableLocator(_temporaryDirectory).Find("qemu-test");

        Assert.Equal(executable, result);
    }

    [Fact]
    public void IgnoresNonExecutableFile()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(_temporaryDirectory);
        var executable = Path.Combine(_temporaryDirectory, "qemu-test");
        File.WriteAllText(executable, "not executable");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead);

        Assert.Null(new ExecutableLocator(_temporaryDirectory).Find("qemu-test"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
