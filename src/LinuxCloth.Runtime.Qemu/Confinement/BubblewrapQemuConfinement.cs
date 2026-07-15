using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Confinement;

/// <summary>
/// Wraps a generated QEMU command in a least-privilege Bubblewrap filesystem namespace.
/// QEMU receives a private network namespace and reaches the separately sandboxed network
/// backend only through the pathname-based passt Unix socket in the session directory.
/// </summary>
public static class BubblewrapQemuConfinement
{
    private const string KvmDevicePath = "/dev/kvm";
    private const string RandomDevicePath = "/dev/urandom";
    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    public static ProcessSpec Wrap(
        ProcessSpec qemuProcess,
        BubblewrapQemuConfinementOptions options)
    {
        ArgumentNullException.ThrowIfNull(qemuProcess);
        ArgumentNullException.ThrowIfNull(options);

        var resources = Validate(qemuProcess, options);
        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-all",
            "--clearenv",
            "--setenv", "LC_ALL", resources.Locale,
        };

        AddReadOnlyDirectory(arguments, "/usr", "/usr");
        AddReadOnlyDirectory(arguments, "/etc", "/etc");
        foreach (var destination in CompatibilityRuntimeRoots)
        {
            var source = ResolveCompatibilityRuntimeRoot(destination);
            if (source is not null)
            {
                AddReadOnlyDirectory(arguments, source, destination);
            }
        }

        arguments.Add("--proc");
        arguments.Add("/proc");
        arguments.Add("--dev");
        arguments.Add("/dev");
        arguments.Add("--dev-bind");
        arguments.Add(KvmDevicePath);
        arguments.Add(KvmDevicePath);
        arguments.Add("--tmpfs");
        arguments.Add("/tmp");

        foreach (var parent in BuildDestinationParents(
                     resources.SessionDirectory,
                     resources.BaseImagePath,
                     resources.OvmfCodePath))
        {
            arguments.Add("--dir");
            arguments.Add(parent);
        }

        AddReadOnlyFile(arguments, resources.BaseImagePath);
        AddReadOnlyFile(arguments, resources.OvmfCodePath);

        arguments.Add("--bind");
        arguments.Add(resources.SessionDirectory);
        arguments.Add(resources.SessionDirectory);
        arguments.Add("--chdir");
        arguments.Add(resources.SessionDirectory);
        arguments.Add("--");
        arguments.Add(qemuProcess.FileName);
        arguments.AddRange(qemuProcess.Arguments);

        return new ProcessSpec(
            resources.BubblewrapExecutable,
            arguments,
            resources.SessionDirectory,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = resources.Locale,
            },
            qemuProcess.StandardOutputPath,
            qemuProcess.StandardErrorPath,
            inheritEnvironment: false);
    }

    private static ValidatedResources Validate(
        ProcessSpec qemuProcess,
        BubblewrapQemuConfinementOptions options)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Bubblewrap QEMU confinement is available only on Linux.");
        }

        var bubblewrapExecutable = ConfinementPathGuard.RequireAbsolutePath(
            options.BubblewrapExecutable,
            nameof(options.BubblewrapExecutable));
        if (!string.Equals(Path.GetFileName(bubblewrapExecutable), "bwrap", StringComparison.Ordinal) ||
            !ConfinementPathGuard.IsSameOrDescendant(bubblewrapExecutable, "/usr"))
        {
            throw new ArgumentException(
                "The Bubblewrap executable must be the system bwrap binary beneath /usr.",
                nameof(options));
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(
            bubblewrapExecutable,
            requireLeaf: false,
            nameof(options.BubblewrapExecutable));
        if (Directory.Exists(bubblewrapExecutable))
        {
            throw new ArgumentException("The Bubblewrap executable path cannot be a directory.", nameof(options));
        }

        var qemuExecutable = ConfinementPathGuard.RequireAbsolutePath(qemuProcess.FileName, nameof(qemuProcess));
        if (!string.Equals(Path.GetFileName(qemuExecutable), "qemu-system-x86_64", StringComparison.Ordinal) ||
            !ConfinementPathGuard.IsSameOrDescendant(qemuExecutable, "/usr"))
        {
            throw new ArgumentException(
                "Only the system qemu-system-x86_64 executable beneath /usr can be confined.",
                nameof(qemuProcess));
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(qemuExecutable, requireLeaf: false, nameof(qemuProcess));

        var sessionDirectory = ConfinementPathGuard.RequireAbsolutePath(
            options.SessionDirectory,
            nameof(options.SessionDirectory));
        ConfinementPathGuard.RequireExistingDirectoryWithoutLinks(
            sessionDirectory,
            nameof(options.SessionDirectory));
        if (string.Equals(sessionDirectory, Path.GetPathRoot(sessionDirectory), StringComparison.Ordinal))
        {
            throw new ArgumentException("The filesystem root cannot be used as a session directory.", nameof(options));
        }

        var baseImagePath = ConfinementPathGuard.RequireAbsolutePath(
            options.BaseImagePath,
            nameof(options.BaseImagePath));
        ConfinementPathGuard.RequireExistingRegularFileWithoutLinks(baseImagePath, nameof(options.BaseImagePath));

        var ovmfCodePath = ConfinementPathGuard.RequireAbsolutePath(
            options.OvmfCodePath,
            nameof(options.OvmfCodePath));
        ConfinementPathGuard.RequireExistingRegularFileWithoutLinks(ovmfCodePath, nameof(options.OvmfCodePath));

        if (ConfinementPathGuard.IsSameOrDescendant(baseImagePath, sessionDirectory) ||
            ConfinementPathGuard.IsSameOrDescendant(ovmfCodePath, sessionDirectory))
        {
            throw new ArgumentException(
                "Read-only base image and firmware resources must be outside the writable session directory.",
                nameof(options));
        }

        var workingDirectory = qemuProcess.WorkingDirectory is null
            ? null
            : ConfinementPathGuard.RequireAbsolutePath(qemuProcess.WorkingDirectory, nameof(qemuProcess));
        if (!string.Equals(workingDirectory, sessionDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The QEMU working directory must be the exact writable session directory.",
                nameof(qemuProcess));
        }

        ValidateLogPath(qemuProcess.StandardOutputPath, sessionDirectory, nameof(qemuProcess.StandardOutputPath));
        ValidateLogPath(qemuProcess.StandardErrorPath, sessionDirectory, nameof(qemuProcess.StandardErrorPath));

        if (qemuProcess.InheritEnvironment)
        {
            throw new ArgumentException("A confined QEMU process cannot inherit the host environment.", nameof(qemuProcess));
        }

        var locale = ValidateEnvironment(qemuProcess.Environment);
        ValidateQemuArguments(qemuProcess.Arguments, sessionDirectory, baseImagePath, ovmfCodePath);

        return new ValidatedResources(
            bubblewrapExecutable,
            sessionDirectory,
            baseImagePath,
            ovmfCodePath,
            locale);
    }

    private static string ValidateEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        if (environment.Count == 0)
        {
            return "C";
        }

        if (environment.Count != 1 || !environment.TryGetValue("LC_ALL", out var locale) ||
            locale is not ("C" or "C.UTF-8"))
        {
            throw new ArgumentException(
                "Confined QEMU accepts only an LC_ALL environment entry with C or C.UTF-8.",
                nameof(environment));
        }

        return locale;
    }

    private static void ValidateLogPath(string? path, string sessionDirectory, string parameterName)
    {
        if (path is null)
        {
            return;
        }

        var fullPath = ConfinementPathGuard.RequireAbsolutePath(path, parameterName);
        if (!ConfinementPathGuard.IsSameOrDescendant(fullPath, sessionDirectory))
        {
            throw new ArgumentException("Confined QEMU logs must remain inside the session directory.", parameterName);
        }

        ConfinementPathGuard.RequireNoSymbolicLinkComponents(fullPath, requireLeaf: false, parameterName);
    }

    private static void ValidateQemuArguments(
        IReadOnlyList<string> arguments,
        string sessionDirectory,
        string baseImagePath,
        string ovmfCodePath)
    {
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (argument.IndexOfAny(['\0', '\r', '\n']) >= 0 || ConfinementPathGuard.ContainsTraversalSegment(argument))
            {
                throw new ArgumentException("QEMU arguments cannot contain control characters or path traversal.", nameof(arguments));
            }

            foreach (var candidate in ExtractAbsolutePaths(argument))
            {
                var path = ConfinementPathGuard.RequireAbsolutePath(candidate, nameof(arguments));
                var allowed = string.Equals(path, RandomDevicePath, StringComparison.Ordinal) ||
                              string.Equals(path, baseImagePath, StringComparison.Ordinal) ||
                              string.Equals(path, ovmfCodePath, StringComparison.Ordinal) ||
                              ConfinementPathGuard.IsSameOrDescendant(path, sessionDirectory);
                if (!allowed)
                {
                    throw new ArgumentException(
                        $"QEMU argument references a host path outside the confined resources: {path}",
                        nameof(arguments));
                }

                if (ConfinementPathGuard.IsSameOrDescendant(path, sessionDirectory))
                {
                    ConfinementPathGuard.RequireNoSymbolicLinkComponents(path, requireLeaf: false, nameof(arguments));
                }
            }
        }
    }

    private static IEnumerable<string> ExtractAbsolutePaths(string argument)
    {
        for (var index = 0; index < argument.Length; index++)
        {
            if (argument[index] != '/' ||
                (index > 0 && argument[index - 1] is not ('=' or ':' or ',')))
            {
                continue;
            }

            var value = new System.Text.StringBuilder();
            while (index < argument.Length)
            {
                var character = argument[index];
                if (character == ',')
                {
                    if (index + 1 < argument.Length && argument[index + 1] == ',')
                    {
                        value.Append(',');
                        index += 2;
                        continue;
                    }

                    break;
                }

                value.Append(character);
                index++;
            }

            yield return value.ToString();
            index--;
        }
    }

    private static string[] BuildDestinationParents(params string[] paths)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var current = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(current) && current != Path.GetPathRoot(current))
            {
                if (!IsProvidedMountPath(current))
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

    private static bool IsProvidedMountPath(string path) =>
        path is "/usr" or "/etc" or "/proc" or "/dev" or "/tmp" ||
        ConfinementPathGuard.IsSameOrDescendant(path, "/usr") ||
        ConfinementPathGuard.IsSameOrDescendant(path, "/etc");

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

    private static void AddReadOnlyDirectory(List<string> arguments, string source, string destination)
    {
        arguments.Add("--ro-bind");
        arguments.Add(source);
        arguments.Add(destination);
    }

    private static void AddReadOnlyFile(List<string> arguments, string path)
    {
        arguments.Add("--ro-bind");
        arguments.Add(path);
        arguments.Add(path);
    }

    private sealed record ValidatedResources(
        string BubblewrapExecutable,
        string SessionDirectory,
        string BaseImagePath,
        string OvmfCodePath,
        string Locale);
}
