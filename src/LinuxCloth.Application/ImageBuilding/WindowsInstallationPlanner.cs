using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public sealed record WindowsInstallationImage(
    int Index,
    string DisplayName,
    string EditionId,
    string Architecture,
    int Build)
{
    public bool IsSupported =>
        string.Equals(Architecture, "amd64", StringComparison.Ordinal) && Build >= 22000;

    public WindowsInstallationSelection ToSelection() => new(Index, EditionId, DisplayName);
}

public sealed record WindowsInstallationCatalog(
    IReadOnlyList<WindowsInstallationImage> Images,
    int? SuggestedImageIndex)
{
    public IReadOnlyList<WindowsInstallationImage> SupportedImages =>
        Images.Where(static image => image.IsSupported).ToArray();
}

public interface IWindowsInstallationPlanner
{
    Task<WindowsInstallationCatalog> AnalyzeAsync(
        string windowsIsoPath,
        string xorrisoPath,
        string wimlibImagexPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsInstallationPlanner : IWindowsInstallationPlanner
{
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;

    private static readonly string[] InstallationImagePaths =
    [
        "/sources/install.wim",
        "/sources/install.esd",
        "/SOURCES/INSTALL.WIM",
        "/SOURCES/INSTALL.ESD",
    ];

    private static readonly string[] CompatibilityRuntimeRoots = ["/bin", "/sbin", "/lib", "/lib64"];

    private readonly IProcessRunner _processRunner;
    private readonly string _analysisRoot;

    public WindowsInstallationPlanner(IProcessRunner processRunner, string analysisRoot)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisRoot);
        if (!Path.IsPathFullyQualified(analysisRoot))
        {
            throw new ArgumentException("The Windows media analysis root must be absolute.", nameof(analysisRoot));
        }

        _analysisRoot = Path.GetFullPath(analysisRoot);
    }

    public async Task<WindowsInstallationCatalog> AnalyzeAsync(
        string windowsIsoPath,
        string xorrisoPath,
        string wimlibImagexPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Windows installation media analysis is supported only on Linux.");
        }

        var windowsIso = ImageBuildPathGuard.RequireRegularFile(
            windowsIsoPath,
            "Windows installation ISO");
        var xorriso = ImageBuildPathGuard.RequireRegularFile(xorrisoPath, "xorriso executable", true);
        var wimlib = ImageBuildPathGuard.RequireRegularFile(
            wimlibImagexPath,
            "wimlib-imagex executable",
            true);
        var bubblewrap = ImageBuildPathGuard.RequireRegularFile(
            bubblewrapPath,
            "Bubblewrap executable",
            true);
        EnsurePrivateAnalysisRoot();
        var directory = Path.Combine(_analysisRoot, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        SetPrivateDirectoryMode(directory);

        try
        {
            var installationImage = await ExtractInstallationImageAsync(
                    windowsIso,
                    xorriso,
                    bubblewrap,
                    directory,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await _processRunner.RunAsync(
                    ConfineMetadataTool(
                        bubblewrap,
                        new ProcessSpec(
                            wimlib,
                            ["info", installationImage, "--xml"],
                            directory,
                            new Dictionary<string, string?> { ["LC_ALL"] = "C" }),
                        installationImage),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new WindowsImageBuildException(
                    $"wimlib-imagex could not inspect the Windows installation image: {Truncate(result.StandardError, 1024)}");
            }

            if (Encoding.UTF8.GetByteCount(result.StandardOutput) is <= 0 or > MaximumMetadataBytes)
            {
                throw new WindowsImageBuildException("Windows installation image metadata has an invalid size.");
            }

            return ParseCatalog(result.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                ImageBuildPathGuard.DeleteTreeWithoutFollowingLinks(directory);
            }
        }
    }

    public static WindowsInstallationCatalog ParseCatalog(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        if (Encoding.UTF8.GetByteCount(xml) > MaximumMetadataBytes)
        {
            throw new WindowsImageBuildException("Windows installation image metadata exceeds its size limit.");
        }

        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(
                text,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = MaximumMetadataBytes,
                    XmlResolver = null,
                });
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "WIM", StringComparison.Ordinal))
            {
                throw new WindowsImageBuildException("Windows installation image metadata has an invalid root element.");
            }

            var images = root.Elements()
                .Where(static element => string.Equals(element.Name.LocalName, "IMAGE", StringComparison.Ordinal))
                .Select(ParseImage)
                .OrderBy(static image => image.Index)
                .ToArray();
            if (images.Length == 0 || images.Select(static image => image.Index).Distinct().Count() != images.Length)
            {
                throw new WindowsImageBuildException("Windows installation media contains no uniquely indexed images.");
            }

            var supported = images.Where(static image => image.IsSupported).ToArray();
            if (supported.Length == 0)
            {
                throw new WindowsImageBuildException("Windows installation media contains no Windows 11 amd64 image.");
            }

            return new WindowsInstallationCatalog(
                images,
                supported.Length == 1 ? supported[0].Index : null);
        }
        catch (XmlException exception)
        {
            throw new WindowsImageBuildException("Windows installation image metadata is not valid XML.", exception);
        }
        catch (FormatException exception)
        {
            throw new WindowsImageBuildException("Windows installation image metadata contains an invalid number.", exception);
        }
    }

    public static ProcessSpec BuildExtraction(
        string xorrisoPath,
        string isoPath,
        string isoEntryPath,
        string destinationPath) =>
        new(
            xorrisoPath,
            [
                "-no_rc",
                "-abort_on", "FAILURE",
                "-indev", isoPath,
                "-osirrox", "on:o_excl_off",
                "-extract_single", isoEntryPath, destinationPath,
                "-end",
            ],
            Path.GetDirectoryName(destinationPath),
            new Dictionary<string, string?> { ["LC_ALL"] = "C" });

    private async Task<string> ExtractInstallationImageAsync(
        string windowsIso,
        string xorriso,
        string bubblewrap,
        string directory,
        CancellationToken cancellationToken)
    {
        foreach (var isoPath in InstallationImagePaths)
        {
            var extension = isoPath.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) ? ".esd" : ".wim";
            var destination = Path.Combine(directory, $"install{extension}");
            var result = await _processRunner.RunAsync(
                    ConfineExtractionTool(
                        bubblewrap,
                        BuildExtraction(xorriso, windowsIso, isoPath, destination),
                        windowsIso,
                        directory),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var extracted = ImageBuildPathGuard.RequireRegularFile(
                    destination,
                    "extracted Windows installation image");
                if (new FileInfo(extracted).Length <= 0)
                {
                    throw new WindowsImageBuildException("The extracted Windows installation image is empty.");
                }

                SetPrivateFileMode(extracted);
                return extracted;
            }

            File.Delete(destination);
        }

        throw new WindowsImageBuildException("The Windows ISO installation image could not be extracted for analysis.");
    }

    private static WindowsInstallationImage ParseImage(XElement image)
    {
        var index = int.Parse(
            RequireText(image.Attribute("INDEX")?.Value, "image index"),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        if (index <= 0)
        {
            throw new WindowsImageBuildException("A Windows installation image index must be positive.");
        }

        var windows = RequireChild(image, "WINDOWS");
        var version = RequireChild(windows, "VERSION");
        var architecture = ParseArchitecture(RequireChildText(windows, "ARCH"));
        var editionId = RequireBoundedText(RequireChildText(windows, "EDITIONID"), "edition ID", 128);
        var displayName = FindChildText(image, "DISPLAYNAME") ??
                          FindChildText(image, "NAME") ??
                          throw new WindowsImageBuildException("A Windows installation image has no display name.");
        var build = int.Parse(
            RequireChildText(version, "BUILD"),
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        return new WindowsInstallationImage(
            index,
            RequireBoundedText(displayName, "display name", 256),
            editionId,
            architecture,
            build);
    }

    private static string ParseArchitecture(string value) => value switch
    {
        "9" => "amd64",
        "0" => "x86",
        "12" => "arm64",
        _ => $"unknown-{RequireBoundedText(value, "architecture", 16)}",
    };

    private static XElement RequireChild(XElement parent, string localName) =>
        parent.Elements().SingleOrDefault(
            element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
        ?? throw new WindowsImageBuildException($"Windows installation image metadata is missing {localName}.");

    private static string RequireChildText(XElement parent, string localName) =>
        RequireText(FindChildText(parent, localName), localName);

    private static string? FindChildText(XElement parent, string localName) =>
        parent.Elements().SingleOrDefault(
            element => string.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))?.Value;

    private static string RequireText(string? value, string description) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new WindowsImageBuildException($"Windows installation image metadata is missing {description}.")
            : value;

    private static string RequireBoundedText(string value, string description, int maximumLength)
    {
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new WindowsImageBuildException($"Windows installation image {description} is invalid.");
        }

        return value;
    }

    private static ProcessSpec ConfineExtractionTool(
        string bubblewrap,
        ProcessSpec process,
        string isoPath,
        string writableDirectory) =>
        ConfineTool(bubblewrap, process, [isoPath], [writableDirectory]);

    private static ProcessSpec ConfineMetadataTool(
        string bubblewrap,
        ProcessSpec process,
        string installationImage) =>
        ConfineTool(bubblewrap, process, [installationImage], []);

    private static ProcessSpec ConfineTool(
        string bubblewrap,
        ProcessSpec process,
        IReadOnlyList<string> readOnlyPaths,
        IReadOnlyList<string> writablePaths)
    {
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

        foreach (var path in readOnlyPaths)
        {
            arguments.AddRange(["--ro-bind", path, path]);
        }

        foreach (var path in writablePaths)
        {
            arguments.AddRange(["--bind", path, path]);
        }

        if (!IsProvidedBySystemMount(process.FileName))
        {
            arguments.AddRange(["--ro-bind", process.FileName, process.FileName]);
        }

        arguments.Add("--");
        arguments.Add(process.FileName);
        arguments.AddRange(process.Arguments);
        return new ProcessSpec(
            bubblewrap,
            arguments,
            process.WorkingDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C" },
            identityExecutablePath: process.FileName);
    }

    private void EnsurePrivateAnalysisRoot()
    {
        if (File.Exists(_analysisRoot))
        {
            throw new IOException("A file exists where the Windows media analysis directory is required.");
        }

        if (Directory.Exists(_analysisRoot) &&
            File.GetAttributes(_analysisRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The Windows media analysis directory cannot be a symbolic link.");
        }

        Directory.CreateDirectory(_analysisRoot);
        SetPrivateDirectoryMode(_analysisRoot);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters ? value : value[..maximumCharacters];

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
