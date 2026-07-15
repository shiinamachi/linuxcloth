using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Confinement;

/// <summary>
/// Restricts session sidecars to system libraries and the owned session directory.
/// passt retains the host network namespace because it is the VM's outbound backend;
/// swtpm receives an isolated network namespace.
/// </summary>
public static class BubblewrapSidecarConfinement
{
    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    public static ProcessSpec WrapSwtpm(
        ProcessSpec process,
        BubblewrapQemuConfinementOptions options) =>
        Wrap(process, options, "swtpm", retainHostNetwork: false);

    public static ProcessSpec WrapPasst(
        ProcessSpec process,
        BubblewrapQemuConfinementOptions options) =>
        Wrap(process, options, "passt", retainHostNetwork: true);

    private static ProcessSpec Wrap(
        ProcessSpec process,
        BubblewrapQemuConfinementOptions options,
        string expectedExecutableName,
        bool retainHostNetwork)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Bubblewrap sidecar confinement is available only on Linux.");
        }

        var bubblewrap = ValidateSystemExecutable(options.BubblewrapExecutable, "bwrap", nameof(options));
        var executable = ValidateSystemExecutable(process.FileName, expectedExecutableName, nameof(process));
        var sessionDirectory = ConfinementPathGuard.RequireAbsolutePath(
            options.SessionDirectory,
            nameof(options.SessionDirectory));
        ConfinementPathGuard.RequireExistingDirectoryWithoutLinks(
            sessionDirectory,
            nameof(options.SessionDirectory));

        ValidateProcess(process, expectedExecutableName, sessionDirectory);

        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-all",
        };
        if (retainHostNetwork)
        {
            arguments.Add("--share-net");
        }

        arguments.AddRange(
        [
            "--clearenv",
            "--setenv", "LC_ALL", "C",
            "--ro-bind", "/usr", "/usr",
            "--ro-bind", "/etc", "/etc",
        ]);
        foreach (var destination in CompatibilityRuntimeRoots)
        {
            var source = ResolveCompatibilityRuntimeRoot(destination);
            if (source is not null)
            {
                arguments.AddRange(["--ro-bind", source, destination]);
            }
        }

        arguments.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp"]);
        foreach (var parent in BuildDestinationParents(sessionDirectory))
        {
            arguments.AddRange(["--dir", parent]);
        }

        arguments.AddRange(
        [
            "--bind", sessionDirectory, sessionDirectory,
            "--chdir", sessionDirectory,
            "--",
            executable,
        ]);
        arguments.AddRange(process.Arguments);

        return new ProcessSpec(
            bubblewrap,
            arguments,
            sessionDirectory,
            HostEnvironment.Minimal(),
            process.StandardOutputPath,
            process.StandardErrorPath,
            inheritEnvironment: false,
            identityExecutablePath: executable);
    }

    private static string ValidateSystemExecutable(
        string path,
        string expectedFileName,
        string parameterName)
    {
        var executable = ConfinementPathGuard.RequireAbsolutePath(path, parameterName);
        if (!string.Equals(Path.GetFileName(executable), expectedFileName, StringComparison.Ordinal) ||
            !ConfinementPathGuard.IsSameOrDescendant(executable, "/usr"))
        {
            throw new ArgumentException(
                $"The confined executable must be the system {expectedFileName} binary beneath /usr.",
                parameterName);
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(
            executable,
            requireLeaf: false,
            parameterName);
        if (Directory.Exists(executable))
        {
            throw new ArgumentException("A confined executable cannot be a directory.", parameterName);
        }

        return executable;
    }

    private static void ValidateProcess(
        ProcessSpec process,
        string expectedExecutableName,
        string sessionDirectory)
    {
        if (!string.Equals(
                process.WorkingDirectory is null ? null : Path.GetFullPath(process.WorkingDirectory),
                sessionDirectory,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A confined sidecar must use the exact session directory as its working directory.",
                nameof(process));
        }

        if (process.InheritEnvironment ||
            process.Environment.Count != 1 ||
            !process.Environment.TryGetValue("LC_ALL", out var locale) ||
            !string.Equals(locale, "C", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A confined sidecar accepts only the fixed LC_ALL=C environment.",
                nameof(process));
        }

        ValidateLogPath(process.StandardOutputPath, sessionDirectory);
        ValidateLogPath(process.StandardErrorPath, sessionDirectory);
        if (process.IdentityExecutablePath is not null)
        {
            throw new ArgumentException(
                "The unwrapped sidecar cannot override its process identity.",
                nameof(process));
        }

        if (expectedExecutableName == "swtpm")
        {
            ValidateSwtpmArguments(process.Arguments);
        }
        else
        {
            ValidatePasstArguments(process.Arguments, sessionDirectory);
        }
    }

    private static void ValidateSwtpmArguments(IReadOnlyList<string> arguments)
    {
        string[] expected =
        [
            "socket",
            "--tpm2",
            "--tpmstate", "dir=swtpm,mode=0600,lock",
            "--ctrl", "type=unixio,path=tpm.sock,mode=0600,terminate",
            "--log", "file=-",
        ];
        if (!arguments.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Only the generated private swtpm command can be confined.",
                nameof(arguments));
        }
    }

    private static void ValidatePasstArguments(
        IReadOnlyList<string> arguments,
        string sessionDirectory)
    {
        if (arguments.Count != 13 ||
            !arguments.Take(4).SequenceEqual(
                ["--foreground", "--one-off", "--runas", "0"],
                StringComparer.Ordinal) ||
            !string.Equals(arguments[4], "--socket", StringComparison.Ordinal) ||
            !ConfinementPathGuard.IsSameOrDescendant(arguments[5], sessionDirectory) ||
            !arguments.Skip(6).SequenceEqual(
                [
                    "--no-map-gw",
                    "--no-dhcp-search",
                    "--tcp-ports", "none",
                    "--udp-ports", "none",
                    "--quiet",
                ],
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Only the generated no-forwarding passt command can be confined.",
                nameof(arguments));
        }

        var socketPath = ConfinementPathGuard.RequireAbsolutePath(arguments[5], nameof(arguments));
        ConfinementPathGuard.RequireNoSymbolicLinkComponents(
            socketPath,
            requireLeaf: false,
            nameof(arguments));
    }

    private static void ValidateLogPath(string? path, string sessionDirectory)
    {
        if (path is null)
        {
            return;
        }

        var fullPath = ConfinementPathGuard.RequireAbsolutePath(path, nameof(path));
        if (!ConfinementPathGuard.IsSameOrDescendant(fullPath, sessionDirectory))
        {
            throw new ArgumentException("Sidecar logs must remain inside the session directory.", nameof(path));
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(
            fullPath,
            requireLeaf: false,
            nameof(path));
    }

    private static string[] BuildDestinationParents(string path)
    {
        var parents = new List<string>();
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
        {
            if (!ConfinementPathGuard.IsSameOrDescendant(current, "/usr") &&
                !ConfinementPathGuard.IsSameOrDescendant(current, "/etc"))
            {
                parents.Add(current);
            }

            current = Path.GetDirectoryName(current);
        }

        parents.Reverse();
        return [.. parents];
    }

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

        var target = information.ResolveLinkTarget(returnFinalTarget: true);
        return target is DirectoryInfo directory && directory.Exists
            ? directory.FullName
            : null;
    }
}
