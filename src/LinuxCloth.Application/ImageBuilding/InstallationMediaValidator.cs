using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public interface IInstallationMediaValidator
{
    Task<ImageBuildFileFingerprint> ValidateWindowsAsync(
        string windowsIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default);

    Task<ImageBuildFileFingerprint> ValidateVirtioWinAsync(
        string virtioWinIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default);

    Task<ValidatedInstallationMedia> ValidateAsync(
        string windowsIsoPath,
        string virtioWinIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default);
}

public sealed class SevenZipInstallationMediaValidator : IInstallationMediaValidator
{
    public const long MaximumWindowsIsoBytes = 32L * 1024 * 1024 * 1024;
    public const long MaximumVirtioWinIsoBytes = 8L * 1024 * 1024 * 1024;

    private static readonly (string[] Alternatives, string ErrorMessage)[] WindowsRequirements =
    [
        (
            ["/efi/boot/bootx64.efi", "/EFI/BOOT/BOOTX64.EFI"],
            "The Windows ISO does not contain a Windows x64 UEFI boot image."),
        (
            [
                "/sources/install.wim",
                "/sources/install.esd",
                "/SOURCES/INSTALL.WIM",
                "/SOURCES/INSTALL.ESD",
            ],
            "The Windows ISO does not contain sources/install.wim or sources/install.esd."),
    ];

    private static readonly string[][] VirtioWinRequirements =
    [
        [
            "/vioscsi/w11/amd64/vioscsi.inf",
            "/VIOSCSI/W11/AMD64/VIOSCSI.INF",
        ],
        [
            "/NetKVM/w11/amd64/netkvm.inf",
            "/NETKVM/W11/AMD64/NETKVM.INF",
        ],
    ];

    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    private readonly IProcessRunner _processRunner;

    public SevenZipInstallationMediaValidator(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<ValidatedInstallationMedia> ValidateAsync(
        string windowsIsoPath,
        string virtioWinIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        var windowsFingerprint = await ValidateWindowsAsync(
                windowsIsoPath,
                sevenZipPath,
                bubblewrapPath,
                cancellationToken)
            .ConfigureAwait(false);
        var virtioFingerprint = await ValidateVirtioWinAsync(
                virtioWinIsoPath,
                sevenZipPath,
                bubblewrapPath,
                cancellationToken)
            .ConfigureAwait(false);
        return new ValidatedInstallationMedia(windowsFingerprint, virtioFingerprint);
    }

    public async Task<ImageBuildFileFingerprint> ValidateWindowsAsync(
        string windowsIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        var windowsIso = ValidateIsoFile(
            windowsIsoPath,
            "Windows installation ISO",
            MaximumWindowsIsoBytes);
        var (sevenZip, bubblewrap) = ValidateTools(sevenZipPath, bubblewrapPath);

        foreach (var requirement in WindowsRequirements)
        {
            await RequireAnyIsoEntryAsync(
                    sevenZip,
                    bubblewrap,
                    windowsIso,
                    requirement.Alternatives,
                    requirement.ErrorMessage,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ImageBuildFileHasher.HashAsync(
                windowsIso,
                "Windows installation ISO",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ImageBuildFileFingerprint> ValidateVirtioWinAsync(
        string virtioWinIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        var virtioWinIso = ValidateIsoFile(
            virtioWinIsoPath,
            "virtio-win ISO",
            MaximumVirtioWinIsoBytes);
        var (sevenZip, bubblewrap) = ValidateTools(sevenZipPath, bubblewrapPath);
        foreach (var alternatives in VirtioWinRequirements)
        {
            await RequireAnyIsoEntryAsync(
                    sevenZip,
                    bubblewrap,
                    virtioWinIso,
                    alternatives,
                    "The virtio-win ISO does not contain Windows 11 amd64 storage and network drivers.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ImageBuildFileHasher.HashAsync(
                virtioWinIso,
                "virtio-win ISO",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static ProcessSpec BuildProbe(string sevenZipPath, string isoPath, string entryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sevenZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);
        if (entryPath[0] != '/' ||
            entryPath.Any(char.IsControl) ||
            entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("An ISO entry probe must use an absolute, control-free ISO path.", nameof(entryPath));
        }

        return new ProcessSpec(
            sevenZipPath,
            [
                "l",
                "-slt",
                "-ba",
                "-bd",
                "-spd",
                "--",
                isoPath,
                entryPath[1..],
            ],
            environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" });
    }

    public static ProcessSpec BuildConfinedProbe(
        string bubblewrapPath,
        ProcessSpec probe,
        string isoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bubblewrapPath);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Installation media inspection is supported only on Linux.");
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
        var mountFiles = new[] { isoPath, probe.FileName }
            .Where(static path => !IsProvidedBySystemMount(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var parent in BuildDestinationParents(mountFiles))
        {
            arguments.AddRange(["--dir", parent]);
        }

        arguments.AddRange(["--ro-bind", isoPath, isoPath]);
        if (!IsProvidedBySystemMount(probe.FileName))
        {
            arguments.AddRange(["--ro-bind", probe.FileName, probe.FileName]);
        }

        arguments.Add("--");
        arguments.Add(probe.FileName);
        arguments.AddRange(probe.Arguments);
        return new ProcessSpec(
            bubblewrapPath,
            arguments,
            environment: new Dictionary<string, string?> { ["LC_ALL"] = "C" },
            inheritEnvironment: false,
            identityExecutablePath: probe.FileName);
    }

    private static string ValidateIsoFile(string path, string description, long maximumBytes)
    {
        var fullPath = ImageBuildPathGuard.RequireRegularFile(path, description);
        var length = new FileInfo(fullPath).Length;
        if (length <= 0 || length > maximumBytes)
        {
            throw new WindowsImageBuildException(
                $"The {description} size must be between 1 and {maximumBytes} bytes.");
        }

        return fullPath;
    }

    private static (string SevenZip, string Bubblewrap) ValidateTools(
        string sevenZipPath,
        string bubblewrapPath) =>
        (
            ImageBuildPathGuard.RequireRegularFile(
                sevenZipPath,
                "7-Zip executable",
                requireExecutable: true),
            ImageBuildPathGuard.RequireRegularFile(
                bubblewrapPath,
                "Bubblewrap executable",
                requireExecutable: true));

    private async Task RequireAnyIsoEntryAsync(
        string sevenZipPath,
        string bubblewrapPath,
        string isoPath,
        IReadOnlyList<string> alternatives,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var lastError = string.Empty;
        foreach (var entry in alternatives)
        {
            var result = await _processRunner.RunAsync(
                    BuildConfinedProbe(
                        bubblewrapPath,
                        BuildProbe(sevenZipPath, isoPath, entry),
                        isoPath),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess && IsRegularFileListing(result.StandardOutput, entry[1..]))
            {
                return;
            }

            lastError = Truncate(result.StandardError.Trim(), 512);
        }

        var detail = string.IsNullOrEmpty(lastError) ? string.Empty : $" 7-Zip: {lastError}";
        throw new WindowsImageBuildException(errorMessage + detail);
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static bool IsRegularFileListing(string output, string entryPath)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < lines.Length; index++)
        {
            if (string.Equals(lines[index], $"Path = {entryPath}", StringComparison.Ordinal) &&
                string.Equals(lines[index + 1], "Folder = -", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

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
