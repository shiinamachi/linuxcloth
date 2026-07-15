using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;

namespace LinuxCloth.Runtime.Qemu.Tests.Confinement;

public sealed class BubblewrapQemuConfinementTests : IDisposable
{
    private readonly string _root;
    private readonly string _sessionDirectory;
    private readonly string _baseImagePath;
    private readonly string _ovmfCodePath;

    public BubblewrapQemuConfinementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcbw-{Guid.NewGuid():N}"[..13]);
        _sessionDirectory = Path.Combine(_root, "session with space");
        _baseImagePath = Path.Combine(_root, "images", "base.qcow2");
        _ovmfCodePath = Path.Combine(_root, "firmware", "OVMF_CODE.secboot.fd");

        Directory.CreateDirectory(_sessionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(_baseImagePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_ovmfCodePath)!);
        File.WriteAllBytes(_baseImagePath, [0]);
        File.WriteAllBytes(_ovmfCodePath, [0]);
    }

    [Fact]
    public void WrapsQemuWithExactReadOnlyAndWritableResources()
    {
        var qemu = CreateQemuProcess();

        var confined = BubblewrapQemuConfinement.Wrap(qemu, CreateOptions());

        Assert.Equal("/usr/bin/bwrap", confined.FileName);
        AssertContainsSequence(confined.Arguments, "--die-with-parent", "--new-session", "--unshare-all");
        Assert.DoesNotContain("--share-net", confined.Arguments);
        AssertContainsSequence(confined.Arguments, "--ro-bind", "/usr", "/usr");
        AssertContainsSequence(confined.Arguments, "--ro-bind", "/etc", "/etc");
        AssertContainsSequence(confined.Arguments, "--proc", "/proc");
        AssertContainsSequence(confined.Arguments, "--dev", "/dev");
        AssertContainsSequence(confined.Arguments, "--dev-bind", "/dev/kvm", "/dev/kvm");
        AssertContainsSequence(confined.Arguments, "--tmpfs", "/tmp");
        AssertContainsSequence(confined.Arguments, "--ro-bind", _baseImagePath, _baseImagePath);
        AssertContainsSequence(confined.Arguments, "--ro-bind", _ovmfCodePath, _ovmfCodePath);
        AssertContainsSequence(confined.Arguments, "--bind", _sessionDirectory, _sessionDirectory);
        AssertContainsSequence(confined.Arguments, "--chdir", _sessionDirectory);
        Assert.False(confined.InheritEnvironment);
        Assert.Equal(qemu.FileName, confined.IdentityExecutablePath);
        Assert.Equal("C", confined.Environment["LC_ALL"]);
        Assert.DoesNotContain("HOME", confined.Environment.Keys);
        Assert.DoesNotContain(confined.Arguments, static argument => argument == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    [Fact]
    public void PreservesEveryQemuArgumentWithoutShellJoining()
    {
        var diskPath = Path.Combine(_sessionDirectory, "disk, special.qcow2");
        File.WriteAllBytes(diskPath, [0]);
        var qemu = CreateQemuProcess(
            ["-drive", $"file={diskPath.Replace(",", ",,", StringComparison.Ordinal)}", "-name", "bank session"]);

        var confined = BubblewrapQemuConfinement.Wrap(qemu, CreateOptions());

        var separator = confined.Arguments.ToList().IndexOf("--");
        Assert.True(separator >= 0);
        Assert.Equal(qemu.FileName, confined.Arguments[separator + 1]);
        Assert.Equal(qemu.Arguments, confined.Arguments.Skip(separator + 2));
        Assert.Contains("bank session", confined.Arguments);
        Assert.Contains($"file={diskPath.Replace(",", ",,", StringComparison.Ordinal)}", confined.Arguments);
    }

    [Fact]
    public void AcceptsTheGeneratedSpiceQemuCommand()
    {
        var request = new LaunchRequest([ServiceId.Parse("WooriBank")]);
        var configuration = new QemuLaunchConfiguration(
            new QemuToolchain(
                "/usr/bin/qemu-system-x86_64",
                "/usr/bin/qemu-img",
                "/usr/bin/swtpm",
                "/usr/bin/passt",
                "/usr/bin/remote-viewer"),
            request,
            Guid.NewGuid(),
            Guid.NewGuid(),
            _sessionDirectory,
            Path.Combine(_sessionDirectory, "overlay.qcow2"),
            _ovmfCodePath,
            Path.Combine(_sessionDirectory, "OVMF_VARS.fd"),
            Path.Combine(_sessionDirectory, "tpm.sock"),
            Path.Combine(_sessionDirectory, "qmp.sock"),
            Path.Combine(_sessionDirectory, "spice.sock"),
            Path.Combine(_sessionDirectory, "guest.sock"),
            Path.Combine(_sessionDirectory, "config"),
            Path.Combine(_sessionDirectory, "passt.sock"));
        var qemu = QemuCommandBuilder.Build(configuration);

        var confined = BubblewrapQemuConfinement.Wrap(qemu, CreateOptions());

        var separator = confined.Arguments.ToList().IndexOf("--");
        Assert.Equal(qemu.Arguments, confined.Arguments.Skip(separator + 2));
    }

    [Fact]
    public void RejectsHostPathsOutsideExplicitResources()
    {
        var qemu = CreateQemuProcess(["-drive", "file=/etc/passwd"]);

        var exception = Assert.Throws<ArgumentException>(
            () => BubblewrapQemuConfinement.Wrap(qemu, CreateOptions()));

        Assert.Contains("outside the confined resources", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRelativePathTraversalInQemuOptions()
    {
        var qemu = CreateQemuProcess(["-drive", "file=../../etc/passwd"]);

        Assert.Throws<ArgumentException>(
            () => BubblewrapQemuConfinement.Wrap(qemu, CreateOptions()));
    }

    [Fact]
    public void RejectsEnvironmentOutsideFixedLocale()
    {
        var qemu = new ProcessSpec(
            "/usr/bin/qemu-system-x86_64",
            ["-nodefaults"],
            _sessionDirectory,
            new Dictionary<string, string?> { ["HOME"] = "/home/example" });

        Assert.Throws<ArgumentException>(() => BubblewrapQemuConfinement.Wrap(qemu, CreateOptions()));
    }

    [Fact]
    public void RejectsSymbolicLinksInWritableOrReadOnlyResources()
    {
        var linkedBase = Path.Combine(_root, "images", "linked-base.qcow2");
        File.CreateSymbolicLink(linkedBase, _baseImagePath);
        var options = CreateOptions() with { BaseImagePath = linkedBase };

        var exception = Assert.Throws<ArgumentException>(
            () => BubblewrapQemuConfinement.Wrap(CreateQemuProcess(), options));

        Assert.Contains("Symbolic links", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRelativeExecutablesAndWorkingDirectoriesOutsideSession()
    {
        var relativeBubblewrap = CreateOptions() with { BubblewrapExecutable = "bwrap" };
        Assert.Throws<ArgumentException>(
            () => BubblewrapQemuConfinement.Wrap(CreateQemuProcess(), relativeBubblewrap));

        var original = CreateQemuProcess();
        var outsideWorkingDirectory = new ProcessSpec(
            original.FileName,
            original.Arguments,
            _root,
            original.Environment);
        Assert.Throws<ArgumentException>(
            () => BubblewrapQemuConfinement.Wrap(outsideWorkingDirectory, CreateOptions()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ProcessSpec CreateQemuProcess(IEnumerable<string>? arguments = null) =>
        new(
            "/usr/bin/qemu-system-x86_64",
            arguments ?? ["-object", "rng-random,id=rng0,filename=/dev/urandom"],
            _sessionDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C" },
            Path.Combine(_sessionDirectory, "qemu.stdout.log"),
            Path.Combine(_sessionDirectory, "qemu.stderr.log"));

    private BubblewrapQemuConfinementOptions CreateOptions() =>
        new(
            "/usr/bin/bwrap",
            _sessionDirectory,
            _baseImagePath,
            _ovmfCodePath);

    private static void AssertContainsSequence(IReadOnlyList<string> values, params string[] expected)
    {
        for (var index = 0; index <= values.Count - expected.Length; index++)
        {
            if (values.Skip(index).Take(expected.Length).SequenceEqual(expected))
            {
                return;
            }
        }

        Assert.Fail($"Expected sequence was not found: {string.Join(" | ", expected)}");
    }
}
