using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Qemu;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class QemuCommandBuilderTests
{
    [Fact]
    public void BuildsSecureSpiceCommandWithoutShellComposition()
    {
        var spec = QemuCommandBuilder.Build(CreateConfiguration());

        Assert.Equal("/usr/bin/qemu-system-x86_64", spec.FileName);
        Assert.Contains("-enable-kvm", spec.Arguments);
        Assert.Contains("on,obsolete=deny,elevateprivileges=deny,spawn=deny,resourcecontrol=deny", spec.Arguments);
        Assert.Contains("unix=on,addr=/run/user/1000/linuxcloth/spice.sock,disable-ticketing=on,disable-agent-file-xfer=on,disable-copy-paste=on,gl=off", spec.Arguments);
        Assert.Contains("socket,id=guestbridge,path=/run/user/1000/linuxcloth/guest.sock,server=on,wait=off", spec.Arguments);
        Assert.Contains("virtserialport,bus=virtio-serial0.0,nr=2,chardev=vdagent,name=com.redhat.spice.0", spec.Arguments);
        Assert.Contains("exit-with-parent=on", spec.Arguments);
        Assert.Contains("order=c,menu=off,strict=on", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, static argument => argument.Contains("/bin/sh", StringComparison.Ordinal));
        Assert.Equal("C", spec.Environment["LC_ALL"]);
    }

    [Fact]
    public void NetworkingIsOmittedWhenDisabled()
    {
        var configuration = CreateConfiguration(networkEnabled: false);

        var spec = QemuCommandBuilder.Build(configuration);

        Assert.DoesNotContain("-netdev", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, static argument => argument.Contains("virtio-net-pci", StringComparison.Ordinal));
    }

    [Fact]
    public void PathsAreEscapedForQemuOptionParsing()
    {
        var configuration = CreateConfiguration() with
        {
            OverlayPath = "/run/user/1000/linuxcloth/disk,session.qcow2",
        };

        var spec = QemuCommandBuilder.Build(configuration);

        Assert.Contains(
            "if=none,id=os,format=qcow2,cache=none,discard=unmap,file=/run/user/1000/linuxcloth/disk,,session.qcow2",
            spec.Arguments);
    }

    [Fact]
    public void NewlineInPathIsRejected()
    {
        var configuration = CreateConfiguration() with
        {
            OverlayPath = "/run/user/1000/linuxcloth/disk\n-injected.qcow2",
        };

        Assert.Throws<ArgumentException>(() => QemuCommandBuilder.Build(configuration));
    }

    [Fact]
    public void RdpIsNotSilentlyExposed()
    {
        var request = new LaunchRequest(
            [ServiceId.Parse("WooriBank")],
            displayMode: DisplayMode.Rdp);

        Assert.Throws<NotSupportedException>(
            () => QemuCommandBuilder.Build(CreateConfiguration(request: request)));
    }

    private static QemuLaunchConfiguration CreateConfiguration(
        bool networkEnabled = true,
        LaunchRequest? request = null)
    {
        request ??= new LaunchRequest(
            [ServiceId.Parse("WooriBank")],
            networkEnabled: networkEnabled);

        return new QemuLaunchConfiguration(
            new QemuToolchain(
                "/usr/bin/qemu-system-x86_64",
                "/usr/bin/qemu-img",
                "/usr/bin/swtpm",
                "/usr/bin/passt",
                "/usr/bin/remote-viewer"),
            request,
            Guid.Parse("b627a831-6f3f-45c7-9487-b38b9a276bf4"),
            Guid.Parse("057f24df-24e0-4815-8652-3f1e8c62e155"),
            "/run/user/1000/linuxcloth",
            "/run/user/1000/linuxcloth/overlay.qcow2",
            "/usr/share/OVMF/OVMF_CODE.secboot.fd",
            "/run/user/1000/linuxcloth/OVMF_VARS.fd",
            "/run/user/1000/linuxcloth/swtpm.sock",
            "/run/user/1000/linuxcloth/qmp.sock",
            "/run/user/1000/linuxcloth/spice.sock",
            "/run/user/1000/linuxcloth/guest.sock",
            "/run/user/1000/linuxcloth/config",
            networkEnabled ? "/run/user/1000/linuxcloth/passt.sock" : null);
    }
}
