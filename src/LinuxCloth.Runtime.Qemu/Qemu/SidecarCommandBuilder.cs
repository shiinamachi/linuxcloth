using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Qemu;

public static class SidecarCommandBuilder
{
    public static ProcessSpec BuildSwtpm(QemuToolchain toolchain, SessionPaths paths) =>
        new(
            toolchain.Swtpm,
            [
                "socket",
                "--tpm2",
                "--tpmstate", "dir=swtpm,mode=0600,lock",
                "--ctrl", "type=unixio,path=tpm.sock,mode=0600,terminate",
                "--log", "file=-",
            ],
            paths.SessionDirectory,
            HostEnvironment.Minimal());

    public static ProcessSpec BuildPasst(QemuToolchain toolchain, SessionPaths paths) =>
        new(
            toolchain.Passt ?? throw new InvalidOperationException(
                "passt is unavailable for this offline-only QEMU toolchain."),
            [
                "--foreground",
                "--one-off",
                "--socket", paths.PasstSocketPath,
                "--no-map-gw",
                "--no-dhcp-search",
                "--tcp-ports", "none",
                "--udp-ports", "none",
                "--quiet",
            ],
            paths.SessionDirectory,
            HostEnvironment.Minimal());

    public static ProcessSpec BuildViewer(QemuToolchain toolchain, SessionPaths paths, string windowTitle) =>
        new(
            toolchain.RemoteViewer,
            [
                "--title", windowTitle,
                new Uri($"spice+unix://{paths.SpiceSocketPath}").AbsoluteUri,
            ],
            paths.SessionDirectory,
            HostEnvironment.Desktop());
}
