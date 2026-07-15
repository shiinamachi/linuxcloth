using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SidecarCommandBuilderTests
{
    private static readonly QemuToolchain Toolchain = new(
        "/usr/bin/qemu-system-x86_64",
        "/usr/bin/qemu-img",
        "/usr/bin/swtpm",
        "/usr/bin/passt",
        "/usr/bin/remote-viewer");

    [Fact]
    public void PasstBlocksHostGatewayAndInboundPorts()
    {
        var spec = SidecarCommandBuilder.BuildPasst(Toolchain, CreatePaths());

        Assert.Contains("--no-map-gw", spec.Arguments);
        Assert.Contains("--one-off", spec.Arguments);
        AssertContainsSequence(spec.Arguments, "--runas", "0");
        Assert.Contains("--no-dhcp-search", spec.Arguments);
        Assert.Equal(2, spec.Arguments.Count(static argument => argument == "none"));
        Assert.DoesNotContain(spec.Arguments, static argument => argument.Contains("port-forward", StringComparison.Ordinal));
    }

    [Fact]
    public void OfflineToolchainCannotAccidentallyStartPasst()
    {
        var offline = Toolchain with { Passt = null };

        Assert.Throws<InvalidOperationException>(() =>
            SidecarCommandBuilder.BuildPasst(offline, CreatePaths()));
    }

    [Fact]
    public void SwtpmUsesPrivateSessionState()
    {
        var paths = CreatePaths();

        var spec = SidecarCommandBuilder.BuildSwtpm(Toolchain, paths);

        Assert.Contains("dir=swtpm,mode=0600,lock", spec.Arguments);
        Assert.Contains("type=unixio,path=tpm.sock,mode=0600,terminate", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, static argument => argument.Contains("startup-clear", StringComparison.Ordinal));
    }

    [Fact]
    public void ViewerUsesSpiceUnixUri()
    {
        var spec = SidecarCommandBuilder.BuildViewer(Toolchain, CreatePaths(), "linuxcloth");

        Assert.Equal("spice+unix:///run/user/1000/lc/sessions/84829d837a8041f18a98489967999ac5/spice.sock", spec.Arguments[^1]);
    }

    private static SessionPaths CreatePaths() =>
        SessionPaths.Create("/run/user/1000/lc", Guid.Parse("84829d83-7a80-41f1-8a98-489967999ac5"));

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
