using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SystemProcessLauncherTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}");

    [Fact]
    public async Task StartsWithMinimalEnvironmentAndCapturesBoundedLogs()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var stdout = Path.Combine(_directory, "stdout.log");
        var stderr = Path.Combine(_directory, "stderr.log");
        var spec = new ProcessSpec(
            "/usr/bin/python3",
            ["-c", "import os,time; print('LC_ALL=' + os.environ['LC_ALL']); time.sleep(0.2)"],
            environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" },
            standardOutputPath: stdout,
            standardErrorPath: stderr);

        await using var process = await new SystemProcessLauncher().StartAsync(spec);
        var exitCode = await process.WaitForExitAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal($"LC_ALL=C{Environment.NewLine}", await File.ReadAllTextAsync(stdout));
        Assert.Equal(process.Id, process.Identity.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(process.Identity.BootId));
        Assert.True(process.Identity.StartTicks > 0);
    }

    [Fact]
    public async Task SendsSigtermBeforeForcingAProcess()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/sleep"))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var spec = new ProcessSpec("/usr/bin/sleep", ["30"]);
        await using var process = await new SystemProcessLauncher().StartAsync(spec);

        await process.TerminateAsync();
        var exitCode = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotEqual(0, exitCode);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task WaitsForWrapperToExecTheExpectedIdentity()
    {
        if (!OperatingSystem.IsLinux() ||
            !File.Exists("/usr/bin/sh") ||
            !File.Exists("/usr/bin/sleep"))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var spec = new ProcessSpec(
            "/usr/bin/sh",
            ["-c", "exec /usr/bin/sleep 30"],
            identityExecutablePath: "/usr/bin/sleep");

        await using var process = await new SystemProcessLauncher().StartAsync(spec);

        Assert.Equal("/usr/bin/sleep", process.Identity.ExecutablePath);
        await process.TerminateAsync();
        _ = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FindsAndControlsExpectedIdentityInSupervisedProcessTree()
    {
        if (!OperatingSystem.IsLinux() ||
            !File.Exists("/usr/bin/sh") ||
            !File.Exists("/usr/bin/sleep"))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var spec = new ProcessSpec(
            "/usr/bin/sh",
            ["-c", "/usr/bin/sh -c '/usr/bin/sleep 30 & wait' & wait"],
            identityExecutablePath: "/usr/bin/sleep");

        await using var process = await new SystemProcessLauncher().StartAsync(spec);

        Assert.Equal("/usr/bin/sleep", process.Identity.ExecutablePath);
        Assert.Equal(process.Identity.ProcessId, process.Id);
        await process.TerminateAsync();
        _ = await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task PreservesLogsWhenProcessExitsBeforeExpectedIdentityAppears()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/sh"))
        {
            return;
        }

        Directory.CreateDirectory(_directory);
        var stderr = Path.Combine(_directory, "early.stderr.log");
        var spec = new ProcessSpec(
            "/usr/bin/sh",
            ["-c", "printf 'identity setup failed\\n' >&2; exit 7"],
            standardErrorPath: stderr,
            identityExecutablePath: "/usr/bin/sleep");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemProcessLauncher().StartAsync(spec));

        Assert.Equal($"identity setup failed{Environment.NewLine}", await File.ReadAllTextAsync(stderr));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
