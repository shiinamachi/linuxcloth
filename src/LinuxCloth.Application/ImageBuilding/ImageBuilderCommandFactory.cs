using System.Globalization;
using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;

namespace LinuxCloth.Application.ImageBuilding;

public static class ImageBuilderCommandFactory
{
    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    public static ProcessSpec BuildQemuImgCreate(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new ProcessSpec(
            workspace.State.Toolchain.QemuImg,
            [
                "create",
                "-q",
                "-f", "qcow2",
                "-o", "compat=1.1,lazy_refcounts=on,cluster_size=2M,preallocation=metadata",
                workspace.Staging.BaseImagePath,
                $"{workspace.State.DiskSizeGiB.ToString(CultureInfo.InvariantCulture)}G",
            ],
            workspace.Staging.DirectoryPath,
            MinimalEnvironment());
    }

    public static ProcessSpec BuildQemuImgCheck(WindowsImageBuildWorkspace workspace) =>
        new(
            workspace.State.Toolchain.QemuImg,
            ["check", "-q", "-f", "qcow2", workspace.Staging.BaseImagePath],
            workspace.Staging.DirectoryPath,
            MinimalEnvironment());

    public static ProcessSpec BuildQemuImgInfo(WindowsImageBuildWorkspace workspace) =>
        new(
            workspace.State.Toolchain.QemuImg,
            ["info", "--output=json", workspace.Staging.BaseImagePath],
            workspace.Staging.DirectoryPath,
            MinimalEnvironment());

    public static ProcessSpec ConfineQemuImg(
        WindowsImageBuildWorkspace workspace,
        ProcessSpec qemuImgProcess)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(qemuImgProcess);
        var operation = qemuImgProcess.Arguments.Count == 0 ? null : qemuImgProcess.Arguments[0];
        var expected = operation switch
        {
            "create" => BuildQemuImgCreate(workspace),
            "check" => BuildQemuImgCheck(workspace),
            "info" => BuildQemuImgInfo(workspace),
            _ => throw new ArgumentException(
                "Only a generated image-builder qemu-img operation can be confined.",
                nameof(qemuImgProcess)),
        };
        if (!MatchesGeneratedProcess(qemuImgProcess, expected))
        {
            throw new ArgumentException(
                "Only a generated image-builder qemu-img operation can be confined.",
                nameof(qemuImgProcess));
        }

        var createsImage = string.Equals(qemuImgProcess.Arguments[0], "create", StringComparison.Ordinal);
        return ConfineTool(
            workspace,
            qemuImgProcess,
            readOnlyPaths: createsImage ? [] : [workspace.Staging.BaseImagePath],
            writablePaths: createsImage ? [workspace.Staging.DirectoryPath] : [],
            identityExecutablePath: qemuImgProcess.FileName);
    }

    public static ProcessSpec BuildSwtpm(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new ProcessSpec(
            workspace.State.Toolchain.Swtpm,
            [
                "socket",
                "--tpm2",
                "--tpmstate", $"dir={workspace.Staging.SwtpmStateTemplateDirectory},mode=0600,lock",
                "--ctrl", $"type=unixio,path={workspace.SwtpmSocketPath},mode=0600,terminate",
                "--flags", "startup-clear",
                "--log", $"file={Path.Combine(workspace.SocketsDirectory, "swtpm.log")}",
            ],
            workspace.SocketsDirectory,
            MinimalEnvironment(),
            Path.Combine(workspace.RuntimeDirectory, "swtpm.stdout.log"),
            Path.Combine(workspace.RuntimeDirectory, "swtpm.stderr.log"));
    }

    public static ProcessSpec BuildQemu(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.State;
        var staging = workspace.Staging;
        var arguments = new List<string>
        {
            "-nodefaults",
            "-no-user-config",
            "-run-with", "exit-with-parent=on",
            "-enable-kvm",
            "-machine", "q35,accel=kvm,smm=on,vmport=off",
            "-cpu", "host,hv_relaxed=on,hv_vapic=on,hv_spinlocks=0x1fff,hv_time=on",
            "-smp", $"{state.CpuCount.ToString(CultureInfo.InvariantCulture)},sockets=1,cores={state.CpuCount.ToString(CultureInfo.InvariantCulture)},threads=1",
            "-m", state.MemoryMiB.ToString(CultureInfo.InvariantCulture),
            "-name", $"linuxcloth-image-{state.ImageId.Value}",
            "-uuid", state.MachineId.ToString("D"),
            "-rtc", "base=localtime,clock=host",
            "-boot", "order=dc,menu=on,strict=on",
            "-global", "ICH9-LPC.disable_s3=1",
            "-global", "driver=cfi.pflash01,property=secure,value=on",
            "-drive", Drive("if=pflash,format=raw,unit=0,readonly=on,file=", state.OvmfCode.Path),
            "-drive", Drive("if=pflash,format=raw,unit=1,file=", staging.OvmfVariablesTemplatePath),
            "-chardev", $"socket,id=chrtpm,path={Escape(workspace.SwtpmSocketPath)}",
            "-tpmdev", "emulator,id=tpm0,chardev=chrtpm",
            "-device", "tpm-tis,tpmdev=tpm0",
            "-object", "rng-random,id=rng0,filename=/dev/urandom",
            "-device", "virtio-rng-pci,rng=rng0",
            "-device", "virtio-scsi-pci,id=scsi0",
            "-drive", Drive("if=none,id=os,format=qcow2,cache=none,discard=unmap,file=", staging.BaseImagePath),
            "-device", "scsi-hd,drive=os,bootindex=2",
            "-device", "qemu-xhci,id=xhci",
            "-device", "usb-tablet,bus=xhci.0",
            "-drive", Drive("if=none,id=windows-install,format=raw,media=cdrom,readonly=on,file=", state.WindowsIso.Path),
            "-device", "usb-storage,bus=xhci.0,drive=windows-install,removable=on,bootindex=1",
            "-drive", Drive("if=none,id=virtio-drivers,format=raw,media=cdrom,readonly=on,file=", state.VirtioWinIso.Path),
            "-device", "usb-storage,bus=xhci.0,drive=virtio-drivers,removable=on",
            "-drive", Drive("if=none,id=linuxcloth-provisioning,format=raw,media=cdrom,readonly=on,file=", workspace.ProvisioningIsoPath),
            "-device", "usb-storage,bus=xhci.0,drive=linuxcloth-provisioning,removable=on",
            "-qmp", $"unix:{Escape(workspace.QmpSocketPath)},server=on,wait=off",
            "-display", "none",
            "-spice", $"unix=on,addr={Escape(workspace.SpiceSocketPath)},disable-ticketing=on,disable-agent-file-xfer=on,disable-copy-paste=on,gl=off",
            "-device", "qxl-vga,vgamem_mb=64",
            "-monitor", "none",
            "-serial", "none",
            "-parallel", "none",
            "-audiodev", "none,id=audio0",
            "-sandbox", "on,obsolete=deny,elevateprivileges=deny,spawn=deny,resourcecontrol=deny",
        };

        return new ProcessSpec(
            state.Toolchain.QemuSystem,
            arguments,
            staging.DirectoryPath,
            MinimalEnvironment(),
            Path.Combine(workspace.RuntimeDirectory, "qemu.stdout.log"),
            Path.Combine(workspace.RuntimeDirectory, "qemu.stderr.log"));
    }

    public static ProcessSpec BuildVerificationQemu(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.State;
        var staging = workspace.Staging;
        var arguments = new List<string>
        {
            "-nodefaults",
            "-no-user-config",
            "-run-with", "exit-with-parent=on",
            "-enable-kvm",
            "-machine", "q35,accel=kvm,smm=on,vmport=off",
            "-cpu", "host,hv_relaxed=on,hv_vapic=on,hv_spinlocks=0x1fff,hv_time=on",
            "-smp", $"{state.CpuCount.ToString(CultureInfo.InvariantCulture)},sockets=1,cores={state.CpuCount.ToString(CultureInfo.InvariantCulture)},threads=1",
            "-m", state.MemoryMiB.ToString(CultureInfo.InvariantCulture),
            "-name", $"linuxcloth-image-verify-{state.ImageId.Value}",
            "-uuid", state.MachineId.ToString("D"),
            "-rtc", "base=localtime,clock=host",
            "-boot", "order=c,menu=off,strict=on",
            "-global", "ICH9-LPC.disable_s3=1",
            "-global", "driver=cfi.pflash01,property=secure,value=on",
            "-drive", Drive("if=pflash,format=raw,unit=0,readonly=on,file=", state.OvmfCode.Path),
            "-drive", Drive("if=pflash,format=raw,unit=1,file=", staging.OvmfVariablesTemplatePath),
            "-chardev", $"socket,id=chrtpm,path={Escape(workspace.SwtpmSocketPath)}",
            "-tpmdev", "emulator,id=tpm0,chardev=chrtpm",
            "-device", "tpm-tis,tpmdev=tpm0",
            "-object", "rng-random,id=rng0,filename=/dev/urandom",
            "-device", "virtio-rng-pci,rng=rng0",
            "-device", "virtio-scsi-pci,id=scsi0",
            "-drive", Drive("if=none,id=os,format=qcow2,cache=none,discard=unmap,file=", staging.BaseImagePath),
            "-device", "scsi-hd,drive=os,bootindex=1",
            "-device", "qemu-xhci,id=xhci",
            "-drive", Drive("if=none,id=linuxcloth-verification,format=raw,file=fat:rw:", workspace.VerificationDirectory),
            "-device", "usb-storage,bus=xhci.0,drive=linuxcloth-verification,removable=on",
            "-qmp", $"unix:{Escape(workspace.QmpSocketPath)},server=on,wait=off",
            "-display", "none",
            "-spice", $"unix=on,addr={Escape(workspace.SpiceSocketPath)},disable-ticketing=on,disable-agent-file-xfer=on,disable-copy-paste=on,gl=off",
            "-device", "qxl-vga,vgamem_mb=64",
            "-monitor", "none",
            "-serial", "none",
            "-parallel", "none",
            "-audiodev", "none,id=audio0",
            "-sandbox", "on,obsolete=deny,elevateprivileges=deny,spawn=deny,resourcecontrol=deny",
        };

        return new ProcessSpec(
            state.Toolchain.QemuSystem,
            arguments,
            staging.DirectoryPath,
            MinimalEnvironment(),
            Path.Combine(workspace.RuntimeDirectory, "qemu.stdout.log"),
            Path.Combine(workspace.RuntimeDirectory, "qemu.stderr.log"));
    }

    public static ProcessSpec BuildProvisioningIso(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var xorriso = new ProcessSpec(
            workspace.State.Toolchain.Xorriso,
            [
                "-no_rc",
                "-as", "mkisofs",
                "-quiet",
                "-V", "LINUXCLOTH",
                "-J",
                "-joliet-long",
                "-r",
                "-o", workspace.ProvisioningIsoPath,
                workspace.ProvisioningSourceDirectory,
            ],
            workspace.RuntimeDirectory,
            MinimalEnvironment(),
            Path.Combine(workspace.RuntimeDirectory, "xorriso.stdout.log"),
            Path.Combine(workspace.RuntimeDirectory, "xorriso.stderr.log"));
        return ConfineTool(
            workspace,
            xorriso,
            readOnlyPaths: [workspace.ProvisioningSourceDirectory],
            writablePaths: [workspace.RuntimeDirectory]);
    }

    public static ProcessSpec BuildViewer(WindowsImageBuildWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new ProcessSpec(
            workspace.State.Toolchain.RemoteViewer,
            [
                "--title", $"linuxcloth — Windows 11 image setup — {workspace.State.ImageId.Value}",
                new Uri($"spice+unix://{workspace.SpiceSocketPath}").AbsoluteUri,
            ],
            workspace.Staging.DirectoryPath,
            HostEnvironment.Desktop(),
            Path.Combine(workspace.RuntimeDirectory, "viewer.stdout.log"),
            Path.Combine(workspace.RuntimeDirectory, "viewer.stderr.log"));
    }

    public static ProcessSpec ConfineQemu(
        WindowsImageBuildWorkspace workspace,
        ProcessSpec qemuProcess)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(qemuProcess);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Windows image construction is supported only on Linux.");
        }

        var state = workspace.State;
        if (!string.Equals(qemuProcess.FileName, state.Toolchain.QemuSystem, StringComparison.Ordinal) ||
            !string.Equals(qemuProcess.WorkingDirectory, workspace.Staging.DirectoryPath, StringComparison.Ordinal) ||
            qemuProcess.InheritEnvironment)
        {
            throw new ArgumentException("Only the generated image-builder QEMU process can be confined.", nameof(qemuProcess));
        }

        var readOnlyFiles = new List<string>
        {
            state.OvmfCode.Path,
        };
        var installerBoot = qemuProcess.Arguments.Any(
            argument => argument.StartsWith("if=none,id=windows-install,", StringComparison.Ordinal));
        if (installerBoot)
        {
            readOnlyFiles.Add(state.WindowsIso.Path);
            readOnlyFiles.Add(state.VirtioWinIso.Path);
            readOnlyFiles.Add(workspace.ProvisioningIsoPath);
        }
        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-all",
            "--clearenv",
            "--setenv", "LC_ALL", "C",
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/etc", "/etc",
        };

        foreach (var destination in CompatibilityRuntimeRoots)
        {
            var source = ResolveCompatibilityRuntimeRoot(destination);
            if (source is not null)
            {
                arguments.Add("--ro-bind");
                arguments.Add(source);
                arguments.Add(destination);
            }
        }

        arguments.AddRange(["--proc", "/proc", "--dev", "/dev"]);
        arguments.AddRange(["--dev-bind", "/dev/kvm", "/dev/kvm", "--tmpfs", "/tmp"]);

        var mountFiles = readOnlyFiles
            .Append(state.Toolchain.QemuSystem)
            .Where(static path => !IsProvidedBySystemMount(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var parent in BuildDestinationParents(
                     mountFiles
                         .Append(workspace.Staging.BaseImagePath)
                         .Append(workspace.Staging.OvmfVariablesTemplatePath)
                         .Append(workspace.SocketsDirectory)
                         .Append(workspace.VerificationDirectory)))
        {
            arguments.Add("--dir");
            arguments.Add(parent);
        }

        foreach (var file in readOnlyFiles)
        {
            arguments.Add("--ro-bind");
            arguments.Add(file);
            arguments.Add(file);
        }

        if (!IsProvidedBySystemMount(state.Toolchain.QemuSystem))
        {
            arguments.Add("--ro-bind");
            arguments.Add(state.Toolchain.QemuSystem);
            arguments.Add(state.Toolchain.QemuSystem);
        }

        arguments.AddRange(["--bind", workspace.Staging.BaseImagePath, workspace.Staging.BaseImagePath]);
        arguments.AddRange(
            [
                "--bind",
                workspace.Staging.OvmfVariablesTemplatePath,
                workspace.Staging.OvmfVariablesTemplatePath,
            ]);
        arguments.AddRange(["--bind", workspace.SocketsDirectory, workspace.SocketsDirectory]);
        if (Directory.Exists(workspace.VerificationDirectory))
        {
            arguments.AddRange(
                ["--bind", workspace.VerificationDirectory, workspace.VerificationDirectory]);
        }
        arguments.Add("--chdir");
        arguments.Add(workspace.Staging.DirectoryPath);
        arguments.Add("--");
        arguments.Add(qemuProcess.FileName);
        arguments.AddRange(qemuProcess.Arguments);

        return new ProcessSpec(
            state.Toolchain.Bubblewrap,
            arguments,
            workspace.Staging.DirectoryPath,
            MinimalEnvironment(),
            qemuProcess.StandardOutputPath,
            qemuProcess.StandardErrorPath,
            inheritEnvironment: false,
            identityExecutablePath: qemuProcess.FileName);
    }

    public static ProcessSpec ConfineSwtpm(
        WindowsImageBuildWorkspace workspace,
        ProcessSpec swtpmProcess)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(swtpmProcess);
        if (!string.Equals(swtpmProcess.FileName, workspace.State.Toolchain.Swtpm, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only the generated image-builder swtpm process can be confined.", nameof(swtpmProcess));
        }

        return ConfineTool(
            workspace,
            swtpmProcess,
            readOnlyPaths: [],
            writablePaths:
            [
                workspace.Staging.SwtpmStateTemplateDirectory,
                workspace.SocketsDirectory,
            ],
            identityExecutablePath: swtpmProcess.FileName);
    }

    private static ProcessSpec ConfineTool(
        WindowsImageBuildWorkspace workspace,
        ProcessSpec process,
        IReadOnlyList<string> readOnlyPaths,
        IReadOnlyList<string> writablePaths,
        string? identityExecutablePath = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Windows image construction is supported only on Linux.");
        }

        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-all",
            "--clearenv",
            "--setenv", "LC_ALL", "C",
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/etc", "/etc",
        };
        foreach (var destination in CompatibilityRuntimeRoots)
        {
            var source = ResolveCompatibilityRuntimeRoot(destination);
            if (source is not null)
            {
                arguments.AddRange(["--ro-bind", source, destination]);
            }
        }

        arguments.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp"]);
        var mountPaths = readOnlyPaths
            .Concat(writablePaths)
            .Append(process.FileName)
            .Where(static path => !IsProvidedBySystemMount(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var parent in BuildDestinationParents(mountPaths))
        {
            arguments.AddRange(["--dir", parent]);
        }

        foreach (var path in writablePaths)
        {
            arguments.AddRange(["--bind", path, path]);
        }

        foreach (var path in readOnlyPaths)
        {
            arguments.AddRange(["--ro-bind", path, path]);
        }

        if (!IsProvidedBySystemMount(process.FileName))
        {
            arguments.AddRange(["--ro-bind", process.FileName, process.FileName]);
        }

        arguments.Add("--chdir");
        arguments.Add(process.WorkingDirectory ?? workspace.RuntimeDirectory);
        arguments.Add("--");
        arguments.Add(process.FileName);
        arguments.AddRange(process.Arguments);
        return new ProcessSpec(
            workspace.State.Toolchain.Bubblewrap,
            arguments,
            process.WorkingDirectory,
            MinimalEnvironment(),
            process.StandardOutputPath,
            process.StandardErrorPath,
            inheritEnvironment: false,
            identityExecutablePath: identityExecutablePath ?? process.FileName);
    }

    private static Dictionary<string, string?> MinimalEnvironment() =>
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private static bool MatchesGeneratedProcess(ProcessSpec actual, ProcessSpec expected) =>
        string.Equals(actual.FileName, expected.FileName, StringComparison.Ordinal) &&
        actual.Arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal) &&
        string.Equals(actual.WorkingDirectory, expected.WorkingDirectory, StringComparison.Ordinal) &&
        actual.Environment.Count == expected.Environment.Count &&
        actual.Environment.All(pair =>
            expected.Environment.TryGetValue(pair.Key, out var expectedValue) &&
            string.Equals(pair.Value, expectedValue, StringComparison.Ordinal)) &&
        string.Equals(actual.StandardOutputPath, expected.StandardOutputPath, StringComparison.Ordinal) &&
        string.Equals(actual.StandardErrorPath, expected.StandardErrorPath, StringComparison.Ordinal) &&
        actual.InheritEnvironment == expected.InheritEnvironment;

    private static string Drive(string prefix, string path) => prefix + Escape(path);

    private static string Escape(string value) => QemuOptionEscaper.Escape(value);

    private static string[] BuildDestinationParents(IEnumerable<string> paths)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var current = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
            {
                if (!IsProvidedBySystemMount(current))
                {
                    parents.Add(current);
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return parents
            .OrderBy(static path => path.Count(static character => character == '/'))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsProvidedBySystemMount(string path) =>
        path is "/usr" or "/etc" or "/proc" or "/dev" or "/tmp" ||
        path.StartsWith("/usr/", StringComparison.Ordinal) ||
        path.StartsWith("/etc/", StringComparison.Ordinal);

    private static string? ResolveCompatibilityRuntimeRoot(string destination)
    {
        var information = new DirectoryInfo(destination);
        if (!information.Exists)
        {
            return null;
        }

        if (information.LinkTarget is null)
        {
            return destination;
        }

        return information.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo directory && directory.Exists
            ? directory.FullName
            : null;
    }
}
