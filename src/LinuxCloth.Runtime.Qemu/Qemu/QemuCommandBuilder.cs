using System.Security.Cryptography;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Qemu;

public static class QemuCommandBuilder
{
    public static ProcessSpec Build(QemuLaunchConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        var request = configuration.Request;
        var arguments = new List<string>
        {
            "-nodefaults",
            "-no-user-config",
            "-enable-kvm",
            "-machine", "q35,accel=kvm,smm=on,vmport=off",
            "-cpu", "host,hv_relaxed=on,hv_vapic=on,hv_spinlocks=0x1fff,hv_time=on",
            "-smp", $"{request.CpuCount},sockets=1,cores={request.CpuCount},threads=1",
            "-m", request.MemoryMiB.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-name", $"linuxcloth-{configuration.SessionId:N}",
            "-uuid", configuration.MachineId.ToString("D"),
            "-rtc", "base=localtime,clock=host",
            "-global", "ICH9-LPC.disable_s3=1",
            "-global", "driver=cfi.pflash01,property=secure,value=on",
            "-drive", Drive("if=pflash,format=raw,unit=0,readonly=on,file=", configuration.OvmfCodePath),
            "-drive", Drive("if=pflash,format=raw,unit=1,file=", configuration.OvmfVariablesPath),
            "-chardev", $"socket,id=chrtpm,path={Escape(configuration.SwtpmSocketPath)}",
            "-tpmdev", "emulator,id=tpm0,chardev=chrtpm",
            "-device", "tpm-tis,tpmdev=tpm0",
            "-object", "rng-random,id=rng0,filename=/dev/urandom",
            "-device", "virtio-rng-pci,rng=rng0",
            "-device", "virtio-scsi-pci,id=scsi0",
            "-drive", Drive("if=none,id=os,format=qcow2,cache=none,discard=unmap,file=", configuration.OverlayPath),
            "-device", "scsi-hd,drive=os,bootindex=1",
            "-device", "qemu-xhci,id=xhci",
            "-drive", Drive("if=none,id=config,format=raw,readonly=on,file=fat:", configuration.ConfigDirectory),
            "-device", "usb-storage,drive=config,removable=on",
            "-qmp", $"unix:{Escape(configuration.QmpSocketPath)},server=on,wait=off",
            "-monitor", "none",
            "-serial", "none",
            "-audiodev", "none,id=audio0",
            "-sandbox", "on,obsolete=deny,elevateprivileges=deny,spawn=deny,resourcecontrol=deny",
        };

        AddNetwork(arguments, configuration);
        AddDisplay(arguments, configuration);

        return new ProcessSpec(
            configuration.Toolchain.QemuSystem,
            arguments,
            configuration.SessionDirectory,
            new Dictionary<string, string?>
            {
                ["LC_ALL"] = "C",
            });
    }

    private static void AddNetwork(List<string> arguments, QemuLaunchConfiguration configuration)
    {
        if (!configuration.Request.NetworkEnabled)
        {
            return;
        }

        arguments.Add("-netdev");
        arguments.Add($"stream,id=net0,server=off,addr.type=unix,addr.path={Escape(configuration.PasstSocketPath!)}");
        arguments.Add("-device");
        arguments.Add($"virtio-net-pci,netdev=net0,mac={CreateMacAddress(configuration.MachineId)}");
    }

    private static void AddDisplay(List<string> arguments, QemuLaunchConfiguration configuration)
    {
        switch (configuration.Request.DisplayMode)
        {
            case DisplayMode.Spice:
                arguments.Add("-display");
                arguments.Add("none");
                arguments.Add("-spice");
                arguments.Add(
                    $"unix=on,addr={Escape(configuration.SpiceSocketPath)},disable-ticketing=on,disable-agent-file-xfer=on,disable-copy-paste={(configuration.Request.ClipboardEnabled ? "off" : "on")}");
                arguments.Add("-device");
                arguments.Add("qxl-vga,vgamem_mb=64");
                break;
            case DisplayMode.QemuConsole:
                arguments.Add("-display");
                arguments.Add("gtk,gl=off");
                arguments.Add("-device");
                arguments.Add("qxl-vga,vgamem_mb=64");
                break;
            case DisplayMode.Rdp:
                throw new NotSupportedException("RDP is not available until an authenticated, policy-controlled transport is configured.");
            default:
                throw new ArgumentOutOfRangeException(nameof(configuration), "Unknown display mode.");
        }
    }

    private static void Validate(QemuLaunchConfiguration configuration)
    {
        var paths = new[]
        {
            configuration.Toolchain.QemuSystem,
            configuration.SessionDirectory,
            configuration.OverlayPath,
            configuration.OvmfCodePath,
            configuration.OvmfVariablesPath,
            configuration.SwtpmSocketPath,
            configuration.QmpSocketPath,
            configuration.SpiceSocketPath,
            configuration.ConfigDirectory,
        };

        if (paths.Any(path => !Path.IsPathFullyQualified(path)))
        {
            throw new ArgumentException("All QEMU executable and resource paths must be absolute.", nameof(configuration));
        }

        if (configuration.Request.NetworkEnabled &&
            (configuration.PasstSocketPath is null || !Path.IsPathFullyQualified(configuration.PasstSocketPath)))
        {
            throw new ArgumentException("An absolute passt socket path is required when networking is enabled.", nameof(configuration));
        }

        foreach (var socketPath in new[]
                 {
                     configuration.SwtpmSocketPath,
                     configuration.QmpSocketPath,
                     configuration.SpiceSocketPath,
                     configuration.PasstSocketPath,
                 }.Where(static path => path is not null))
        {
            if (System.Text.Encoding.UTF8.GetByteCount(socketPath!) > 100)
            {
                throw new ArgumentException($"Unix socket path is too long: {socketPath}", nameof(configuration));
            }
        }
    }

    private static string CreateMacAddress(Guid machineId)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(machineId.ToByteArray(), hash);
        return $"52:54:00:{hash[0]:x2}:{hash[1]:x2}:{hash[2]:x2}";
    }

    private static string Drive(string prefix, string path) => prefix + Escape(path);

    private static string Escape(string value) => QemuOptionEscaper.Escape(value);
}
