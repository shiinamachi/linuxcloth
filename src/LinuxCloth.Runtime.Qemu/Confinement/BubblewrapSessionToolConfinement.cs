using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Confinement;

public static class BubblewrapSessionToolConfinement
{
    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    public static ProcessSpec WrapQemuImg(
        ProcessSpec process,
        string bubblewrapExecutable,
        string sessionDirectory,
        string baseImagePath)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Session image confinement is available only on Linux.");
        }

        var bubblewrap = ValidateSystemExecutable(bubblewrapExecutable, "bwrap", nameof(bubblewrapExecutable));
        var qemuImg = ValidateSystemExecutable(process.FileName, "qemu-img", nameof(process));
        var session = ConfinementPathGuard.RequireAbsolutePath(sessionDirectory, nameof(sessionDirectory));
        var baseImage = ConfinementPathGuard.RequireAbsolutePath(baseImagePath, nameof(baseImagePath));
        ConfinementPathGuard.RequireExistingDirectoryWithoutLinks(session, nameof(sessionDirectory));
        ConfinementPathGuard.RequireExistingRegularFileWithoutLinks(baseImage, nameof(baseImagePath));
        if (ConfinementPathGuard.IsSameOrDescendant(baseImage, session))
        {
            throw new ArgumentException(
                "The read-only base image must remain outside the writable session directory.",
                nameof(baseImagePath));
        }

        ValidateGeneratedCommand(process, session, baseImage);

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
        foreach (var parent in BuildDestinationParents(session, baseImage))
        {
            arguments.AddRange(["--dir", parent]);
        }

        arguments.AddRange(
        [
            "--ro-bind", baseImage, baseImage,
            "--bind", session, session,
            "--chdir", session,
            "--",
            qemuImg,
        ]);
        arguments.AddRange(process.Arguments);

        return new ProcessSpec(
            bubblewrap,
            arguments,
            session,
            HostEnvironment.Minimal(),
            process.StandardOutputPath,
            process.StandardErrorPath,
            inheritEnvironment: false,
            identityExecutablePath: qemuImg);
    }

    private static void ValidateGeneratedCommand(
        ProcessSpec process,
        string sessionDirectory,
        string baseImagePath)
    {
        if (!string.Equals(
                process.WorkingDirectory is null ? null : Path.GetFullPath(process.WorkingDirectory),
                sessionDirectory,
                StringComparison.Ordinal) ||
            process.InheritEnvironment ||
            process.IdentityExecutablePath is not null ||
            process.Environment.Count != 1 ||
            !process.Environment.TryGetValue("LC_ALL", out var locale) ||
            !string.Equals(locale, "C", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The qemu-img process does not match the private session environment.",
                nameof(process));
        }

        if (process.StandardOutputPath is not null || process.StandardErrorPath is not null)
        {
            throw new ArgumentException(
                "The generated qemu-img process must use bounded captured output.",
                nameof(process));
        }

        if (process.Arguments.Count != 9 ||
            !process.Arguments.Take(8).SequenceEqual(
                ["create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", baseImagePath],
                StringComparer.Ordinal) ||
            !string.Equals(process.Arguments[8], Path.Combine(sessionDirectory, "overlay.qcow2"), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only the generated non-committing qcow2 overlay command can be confined.",
                nameof(process));
        }
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
                $"The executable must be the system {expectedFileName} binary beneath /usr.",
                parameterName);
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(executable, requireLeaf: false, parameterName);
        if (Directory.Exists(executable))
        {
            throw new ArgumentException("A confined executable cannot be a directory.", parameterName);
        }

        return executable;
    }

    private static string[] BuildDestinationParents(params string[] paths)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
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
        }

        return parents
            .OrderBy(static path => path.Count(static character => character == '/'))
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
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
