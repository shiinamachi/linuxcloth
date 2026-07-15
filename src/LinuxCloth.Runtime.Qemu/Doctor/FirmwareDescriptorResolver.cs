using System.Text.Json;

namespace LinuxCloth.Runtime.Qemu.Doctor;

public sealed class FirmwareDescriptorResolver
{
    public const string DefaultDescriptorDirectory = "/usr/share/qemu/firmware";
    public const int MaximumDescriptorBytes = 128 * 1024;
    public const int MaximumDescriptorCount = 256;

    private static readonly string[] RequiredFeatures =
    [
        "secure-boot",
        "enrolled-keys",
        "requires-smm",
    ];

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    private readonly string _descriptorDirectory;

    public FirmwareDescriptorResolver(string descriptorDirectory = DefaultDescriptorDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptorDirectory);
        _descriptorDirectory = descriptorDirectory;
    }

    public FirmwareResolution Resolve()
    {
        var diagnostics = new List<FirmwareDiagnostic>();
        if (!Directory.Exists(_descriptorDirectory))
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.DescriptorDirectoryNotFound,
                $"The firmware descriptor directory '{_descriptorDirectory}' does not exist."));
            return CreateResolution(null, diagnostics);
        }

        var descriptorPaths = EnumerateDescriptorPaths(diagnostics);
        if (descriptorPaths is null)
        {
            return CreateResolution(null, diagnostics);
        }

        foreach (var descriptorPath in descriptorPaths)
        {
            var pair = ResolveDescriptor(descriptorPath, diagnostics);
            if (pair is not null)
            {
                return CreateResolution(pair, diagnostics);
            }
        }

        diagnostics.Add(new FirmwareDiagnostic(
            FirmwareDiagnosticCode.NoCompatibleDescriptor,
            "No compatible x86_64 Q35 Secure Boot firmware descriptor was found."));
        return CreateResolution(null, diagnostics);
    }

    private static FirmwareResolution CreateResolution(
        FirmwarePair? pair,
        List<FirmwareDiagnostic> diagnostics) =>
        new(pair, diagnostics.AsReadOnly());

    private List<string>? EnumerateDescriptorPaths(List<FirmwareDiagnostic> diagnostics)
    {
        var paths = new List<string>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _descriptorDirectory,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                if (paths.Count == MaximumDescriptorCount)
                {
                    diagnostics.Add(new FirmwareDiagnostic(
                        FirmwareDiagnosticCode.DescriptorCountLimitExceeded,
                        $"The descriptor directory contains more than the {MaximumDescriptorCount}-file limit."));
                    return null;
                }

                paths.Add(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.DescriptorEnumerationFailed,
                $"Firmware descriptors could not be enumerated: {exception.Message}"));
            return null;
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static FirmwarePair? ResolveDescriptor(
        string descriptorPath,
        List<FirmwareDiagnostic> diagnostics)
    {
        var documentBytes = ReadDescriptor(descriptorPath, diagnostics);
        if (documentBytes is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(documentBytes.Value, JsonOptions);
            return ParseDescriptor(document.RootElement, descriptorPath, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.InvalidJson,
                $"The descriptor is not valid bounded JSON: {exception.Message}",
                descriptorPath));
            return null;
        }
    }

    private static ReadOnlyMemory<byte>? ReadDescriptor(
        string descriptorPath,
        List<FirmwareDiagnostic> diagnostics)
    {
        try
        {
            using var stream = new FileStream(
                descriptorPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (stream.Length > MaximumDescriptorBytes)
            {
                AddTooLargeDiagnostic(descriptorPath, diagnostics);
                return null;
            }

            var buffer = new byte[MaximumDescriptorBytes + 1];
            var bytesRead = 0;
            while (bytesRead < buffer.Length)
            {
                var count = stream.Read(buffer.AsSpan(bytesRead));
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }

            if (bytesRead > MaximumDescriptorBytes)
            {
                AddTooLargeDiagnostic(descriptorPath, diagnostics);
                return null;
            }

            return buffer.AsMemory(0, bytesRead);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.DescriptorReadFailed,
                $"The descriptor could not be read: {exception.Message}",
                descriptorPath));
            return null;
        }
    }

    private static void AddTooLargeDiagnostic(
        string descriptorPath,
        List<FirmwareDiagnostic> diagnostics) =>
        diagnostics.Add(new FirmwareDiagnostic(
            FirmwareDiagnosticCode.DescriptorTooLarge,
            $"The descriptor exceeds the {MaximumDescriptorBytes}-byte limit.",
            descriptorPath));

    private static FirmwarePair? ParseDescriptor(
        JsonElement root,
        string descriptorPath,
        List<FirmwareDiagnostic> diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object || ContainsDuplicateProperty(root))
        {
            AddInvalidDescriptor(
                descriptorPath,
                diagnostics,
                "The root must be an object without duplicate properties.");
            return null;
        }

        if (!TryReadStringArray(root, "interface-types", out var interfaceTypes))
        {
            AddInvalidDescriptor(descriptorPath, diagnostics, "'interface-types' must be a string array.");
            return null;
        }

        if (!interfaceTypes.Contains("uefi", StringComparer.Ordinal))
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.UnsupportedInterface,
                "The descriptor does not advertise the UEFI interface.",
                descriptorPath));
            return null;
        }

        if (!TryReadStringArray(root, "features", out var features))
        {
            AddInvalidDescriptor(descriptorPath, diagnostics, "'features' must be a string array.");
            return null;
        }

        var missingFeatures = RequiredFeatures
            .Where(required => !features.Contains(required, StringComparer.Ordinal))
            .ToArray();
        if (missingFeatures.Length > 0)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.MissingRequiredFeatures,
                $"The descriptor is missing required features: {string.Join(", ", missingFeatures)}.",
                descriptorPath));
            return null;
        }

        if (!TryReadTargets(root, out var supportsTarget))
        {
            AddInvalidDescriptor(descriptorPath, diagnostics, "'targets' must contain well-formed target objects.");
            return null;
        }

        if (!supportsTarget)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.UnsupportedTarget,
                "The descriptor has no x86_64 target compatible with a Q35 machine.",
                descriptorPath));
            return null;
        }

        if (!root.TryGetProperty("mapping", out var mapping) || mapping.ValueKind != JsonValueKind.Object)
        {
            AddInvalidDescriptor(descriptorPath, diagnostics, "'mapping' must be an object.");
            return null;
        }

        if (!TryReadString(mapping, "device", out var device))
        {
            AddInvalidDescriptor(descriptorPath, diagnostics, "'mapping.device' must be a string.");
            return null;
        }

        if (!string.Equals(device, "flash", StringComparison.Ordinal))
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.UnsupportedMapping,
                "Only flash firmware mappings are supported.",
                descriptorPath));
            return null;
        }

        var hasExecutable = TryReadMappingPath(
            mapping,
            "executable",
            out var executablePath,
            out var executableError);
        var hasNvram = TryReadMappingPath(
            mapping,
            "nvram-template",
            out var nvramPath,
            out var nvramError);
        if (!hasExecutable || !hasNvram)
        {
            AddInvalidDescriptor(
                descriptorPath,
                diagnostics,
                executableError ?? nvramError ?? "The firmware mapping is invalid.");
            return null;
        }

        if (!TryNormalizeMappingPath(executablePath, out var normalizedExecutablePath) ||
            !TryNormalizeMappingPath(nvramPath, out var normalizedNvramPath))
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.UnsafeMappingPath,
                "Firmware mapping filenames must be normalized absolute paths without traversal segments.",
                descriptorPath));
            return null;
        }

        return ValidateImages(
            descriptorPath,
            normalizedExecutablePath,
            normalizedNvramPath,
            diagnostics);
    }

    private static bool TryReadTargets(JsonElement root, out bool supportsTarget)
    {
        supportsTarget = false;
        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var target in targets.EnumerateArray())
        {
            if (target.ValueKind != JsonValueKind.Object ||
                !TryReadString(target, "architecture", out var architecture) ||
                !TryReadStringArray(target, "machines", out var machines))
            {
                return false;
            }

            if (string.Equals(architecture, "x86_64", StringComparison.Ordinal) &&
                machines.Any(IsQ35MachinePattern))
            {
                supportsTarget = true;
            }
        }

        return true;
    }

    private static bool IsQ35MachinePattern(string machine) =>
        string.Equals(machine, "q35", StringComparison.Ordinal) ||
        machine.StartsWith("pc-q35-", StringComparison.Ordinal);

    private static bool TryReadMappingPath(
        JsonElement mapping,
        string propertyName,
        out string path,
        out string? error)
    {
        path = string.Empty;
        error = null;

        if (!mapping.TryGetProperty(propertyName, out var image) || image.ValueKind != JsonValueKind.Object)
        {
            error = $"'mapping.{propertyName}' must be an object.";
            return false;
        }

        if (!TryReadString(image, "filename", out path) ||
            !TryReadString(image, "format", out var format))
        {
            error = $"'mapping.{propertyName}' must contain string filename and format values.";
            return false;
        }

        if (!string.Equals(format, "raw", StringComparison.Ordinal))
        {
            error = $"'mapping.{propertyName}.format' must be 'raw'.";
            return false;
        }

        return true;
    }

    private static bool TryNormalizeMappingPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            path.Any(char.IsControl) ||
            (Path.DirectorySeparatorChar == '/' && path.Contains('\\', StringComparison.Ordinal)))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        var relativePart = path[root.Length..];
        var segments = relativePart.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static FirmwarePair? ValidateImages(
        string descriptorPath,
        string executablePath,
        string nvramPath,
        List<FirmwareDiagnostic> diagnostics)
    {
        if (!File.Exists(executablePath) || !File.Exists(nvramPath))
        {
            var missing = new List<string>();
            if (!File.Exists(executablePath))
            {
                missing.Add(executablePath);
            }

            if (!File.Exists(nvramPath))
            {
                missing.Add(nvramPath);
            }

            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.FirmwareFileMissing,
                $"Firmware mapping files do not exist: {string.Join(", ", missing)}.",
                descriptorPath));
            return null;
        }

        long executableSize;
        long nvramSize;
        try
        {
            executableSize = new FileInfo(executablePath).Length;
            nvramSize = new FileInfo(nvramPath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.FirmwareFileInvalid,
                $"Firmware mapping files could not be inspected: {exception.Message}",
                descriptorPath));
            return null;
        }

        if (executableSize <= 0 || nvramSize <= 0)
        {
            diagnostics.Add(new FirmwareDiagnostic(
                FirmwareDiagnosticCode.FirmwareFileInvalid,
                "Firmware mapping files must be non-empty regular files.",
                descriptorPath));
            return null;
        }

        return new FirmwarePair(
            descriptorPath,
            new FirmwareImage(executablePath, executableSize),
            new FirmwareImage(nvramPath, nvramSize));
    }

    private static bool TryReadString(JsonElement owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadStringArray(
        JsonElement owner,
        string propertyName,
        out IReadOnlyList<string> values)
    {
        values = Array.Empty<string>();
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(element.GetString()))
            {
                return false;
            }

            parsed.Add(element.GetString()!);
        }

        values = parsed;
        return true;
    }

    private static bool ContainsDuplicateProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperty(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsDuplicateProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void AddInvalidDescriptor(
        string descriptorPath,
        List<FirmwareDiagnostic> diagnostics,
        string detail) =>
        diagnostics.Add(new FirmwareDiagnostic(
            FirmwareDiagnosticCode.InvalidDescriptor,
            detail,
            descriptorPath));
}
