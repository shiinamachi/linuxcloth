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
                "--tpmstate", $"dir={paths.SwtpmStateDirectory}",
                "--ctrl", $"type=unixio,path={paths.SwtpmSocketPath}",
                "--flags", "not-need-init,startup-clear",
                "--terminate",
            ],
            paths.SessionDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C" });

    public static ProcessSpec BuildPasst(QemuToolchain toolchain, SessionPaths paths) =>
        new(
            toolchain.Passt,
            [
                "--foreground",
                "--socket", paths.PasstSocketPath,
                "--no-map-gw",
                "--tcp-ports", "none",
                "--udp-ports", "none",
                "--quiet",
            ],
            paths.SessionDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C" });

    public static ProcessSpec BuildViewer(QemuToolchain toolchain, SessionPaths paths, string windowTitle) =>
        new(
            toolchain.RemoteViewer,
            [
                "--title", windowTitle,
                new Uri($"spice+unix://{paths.SpiceSocketPath}").AbsoluteUri,
            ],
            paths.SessionDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C.UTF-8" });
}
