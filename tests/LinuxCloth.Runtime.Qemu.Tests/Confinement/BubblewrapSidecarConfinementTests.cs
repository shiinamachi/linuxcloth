using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests.Confinement;

public sealed class BubblewrapSidecarConfinementTests : IDisposable
{
    private readonly string _root;
    private readonly SessionPaths _paths;
    private readonly BubblewrapQemuConfinementOptions _options;
    private readonly QemuToolchain _toolchain = new(
        "/usr/bin/qemu-system-x86_64",
        "/usr/bin/qemu-img",
        "/usr/bin/swtpm",
        "/usr/bin/passt",
        "/usr/bin/remote-viewer");

    public BubblewrapSidecarConfinementTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcsc-{Guid.NewGuid():N}"[..14]);
        _paths = SessionPaths.Create(_root, Guid.NewGuid());
        _paths.CreateDirectories();
        var baseImage = WriteResource("images/base.qcow2");
        var firmware = WriteResource("firmware/OVMF_CODE.fd");
        _options = new BubblewrapQemuConfinementOptions(
            "/usr/bin/bwrap",
            _paths.SessionDirectory,
            baseImage,
            firmware);
    }

    [Fact]
    public void SwtpmUsesPrivateNamespacesAndOnlyTheSessionFilesystem()
    {
        var original = SidecarCommandBuilder.BuildSwtpm(_toolchain, _paths);

        var confined = BubblewrapSidecarConfinement.WrapSwtpm(original, _options);

        Assert.Equal("/usr/bin/bwrap", confined.FileName);
        Assert.Equal("/usr/bin/swtpm", confined.IdentityExecutablePath);
        AssertContainsSequence(confined.Arguments, "--unshare-all", "--clearenv");
        Assert.DoesNotContain("--share-net", confined.Arguments);
        AssertContainsSequence(
            confined.Arguments,
            "--bind", _paths.SessionDirectory, _paths.SessionDirectory);
        AssertCommandPreserved(confined, original);
        Assert.False(confined.InheritEnvironment);
        Assert.Equal("C", confined.Environment["LC_ALL"]);
    }

    [Fact]
    public void PasstRetainsNetworkingButNotTheHostFilesystem()
    {
        var original = SidecarCommandBuilder.BuildPasst(_toolchain, _paths);

        var confined = BubblewrapSidecarConfinement.WrapPasst(original, _options);

        Assert.Equal("/usr/bin/bwrap", confined.FileName);
        Assert.Equal("/usr/bin/passt", confined.IdentityExecutablePath);
        AssertContainsSequence(confined.Arguments, "--unshare-all", "--share-net", "--clearenv");
        AssertContainsSequence(
            confined.Arguments,
            "--bind", _paths.SessionDirectory, _paths.SessionDirectory);
        AssertCommandPreserved(confined, original);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            confined.Arguments);
    }

    [Fact]
    public void RejectsSidecarCommandsThatAddHostPathsOrForwarding()
    {
        var swtpm = SidecarCommandBuilder.BuildSwtpm(_toolchain, _paths);
        var modifiedSwtpm = new ProcessSpec(
            swtpm.FileName,
            swtpm.Arguments.Append("/etc/passwd"),
            swtpm.WorkingDirectory,
            swtpm.Environment);
        Assert.Throws<ArgumentException>(
            () => BubblewrapSidecarConfinement.WrapSwtpm(modifiedSwtpm, _options));

        var passt = SidecarCommandBuilder.BuildPasst(_toolchain, _paths);
        var modifiedArguments = passt.Arguments.ToArray();
        modifiedArguments[^2] = "all";
        var modifiedPasst = new ProcessSpec(
            passt.FileName,
            modifiedArguments,
            passt.WorkingDirectory,
            passt.Environment);
        Assert.Throws<ArgumentException>(
            () => BubblewrapSidecarConfinement.WrapPasst(modifiedPasst, _options));
    }

    [Fact]
    public void RejectsWorkingDirectoryOrEnvironmentOutsideTheSessionPolicy()
    {
        var passt = SidecarCommandBuilder.BuildPasst(_toolchain, _paths);
        var wrongWorkingDirectory = new ProcessSpec(
            passt.FileName,
            passt.Arguments,
            _root,
            passt.Environment);
        Assert.Throws<ArgumentException>(
            () => BubblewrapSidecarConfinement.WrapPasst(wrongWorkingDirectory, _options));

        var ambientEnvironment = new ProcessSpec(
            passt.FileName,
            passt.Arguments,
            passt.WorkingDirectory,
            new Dictionary<string, string?> { ["HOME"] = "/home/example" });
        Assert.Throws<ArgumentException>(
            () => BubblewrapSidecarConfinement.WrapPasst(ambientEnvironment, _options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteResource(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relativePath);
        return path;
    }

    private static void AssertCommandPreserved(ProcessSpec confined, ProcessSpec original)
    {
        var separator = confined.Arguments.ToList().IndexOf("--");
        Assert.True(separator >= 0);
        Assert.Equal(original.FileName, confined.Arguments[separator + 1]);
        Assert.Equal(original.Arguments, confined.Arguments.Skip(separator + 2));
    }

    private static void AssertContainsSequence(
        IReadOnlyList<string> values,
        params string[] expected)
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
