using System.Globalization;
using System.Text.Json;

namespace LinuxCloth.Application.Images;

internal static class ImageMetadataSerializer
{
    private static readonly string[] Version1RootProperties =
    [
        "schemaVersion",
        "imageId",
        "machineId",
        "createdAt",
        "baseImage",
        "ovmfCode",
        "ovmfVariablesTemplate",
        "swtpmStateTemplate",
    ];

    private static readonly string[] Version2RootProperties =
    [
        .. Version1RootProperties,
        "buildProvenance",
    ];

    private static readonly string[] FileProperties =
    [
        "sha256",
        "length",
        "lastWriteUtcTicks",
    ];

    private static readonly string[] ExternalFileProperties =
    [
        "path",
        "sha256",
        "length",
        "lastWriteUtcTicks",
    ];

    private static readonly string[] TreeProperties =
    [
        "sha256",
        "fileCount",
        "totalLength",
        "lastWriteUtcTicks",
    ];

    private static readonly string[] BuildProvenanceProperties =
    [
        "windowsIso",
        "virtioWinIso",
        "guestBridgeExecutable",
        "guestBridgeVersion",
        "windowsArchitecture",
        "windowsBuild",
        "windowsEditionId",
        "windowsDisplayVersion",
        "diskSizeGiB",
        "cpuCount",
        "memoryMiB",
        "evidenceKind",
        "observedAt",
    ];

    public static byte[] Serialize(ManagedImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.SchemaVersion == 1 && metadata.BuildProvenance is not null)
        {
            throw new ImageMetadataValidationException(
                "Image metadata schema version 1 cannot contain build provenance.");
        }

        if (metadata.SchemaVersion is not 1 and not ManagedImageMetadata.CurrentSchemaVersion)
        {
            throw new ImageMetadataValidationException(
                $"Image metadata schema version {metadata.SchemaVersion} is not supported.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", metadata.SchemaVersion);
            writer.WriteString("imageId", metadata.ImageId.Value);
            writer.WriteString("machineId", metadata.MachineId.ToString("D", CultureInfo.InvariantCulture));
            writer.WriteString(
                "createdAt",
                metadata.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            WriteFile(writer, "baseImage", metadata.BaseImage);

            writer.WriteStartObject("ovmfCode");
            writer.WriteString("path", metadata.OvmfCode.Path);
            writer.WriteString("sha256", metadata.OvmfCode.Sha256);
            writer.WriteNumber("length", metadata.OvmfCode.Length);
            writer.WriteNumber("lastWriteUtcTicks", metadata.OvmfCode.LastWriteUtcTicks);
            writer.WriteEndObject();

            WriteFile(writer, "ovmfVariablesTemplate", metadata.OvmfVariablesTemplate);

            writer.WriteStartObject("swtpmStateTemplate");
            writer.WriteString("sha256", metadata.SwtpmStateTemplate.Sha256);
            writer.WriteNumber("fileCount", metadata.SwtpmStateTemplate.FileCount);
            writer.WriteNumber("totalLength", metadata.SwtpmStateTemplate.TotalLength);
            writer.WriteNumber("lastWriteUtcTicks", metadata.SwtpmStateTemplate.LastWriteUtcTicks);
            writer.WriteEndObject();
            if (metadata.SchemaVersion == ManagedImageMetadata.CurrentSchemaVersion)
            {
                WriteBuildProvenance(writer, metadata.BuildProvenance);
            }
            writer.WriteEndObject();
        }

        if (buffer.Length > ImageRegistryLimits.MaximumMetadataBytes)
        {
            throw new ImageMetadataValidationException("The image metadata exceeds its size limit.");
        }

        return buffer.ToArray();
    }

    public static ManagedImageMetadata Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > ImageRegistryLimits.MaximumMetadataBytes)
        {
            throw new ImageMetadataValidationException("The image metadata exceeds its size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var schemaVersion = ReadSchemaVersion(document.RootElement);
            var expectedProperties = schemaVersion switch
            {
                1 => Version1RootProperties,
                ManagedImageMetadata.CurrentSchemaVersion => Version2RootProperties,
                _ => throw new ImageMetadataValidationException(
                    $"Image metadata schema version {schemaVersion} is not supported."),
            };
            var root = ReadStrictObject(document.RootElement, expectedProperties, "metadata");
            if (ReadInt32(root, "schemaVersion", "metadata") != schemaVersion)
            {
                throw new ImageMetadataValidationException("The metadata schemaVersion changed while parsing.");
            }

            var imageId = ParseImageId(ReadString(root, "imageId", "metadata"));
            var machineIdText = ReadString(root, "machineId", "metadata");
            if (!Guid.TryParseExact(machineIdText, "D", out var machineId) || machineId == Guid.Empty)
            {
                throw new ImageMetadataValidationException("The metadata machineId is invalid.");
            }

            var createdAtText = ReadString(root, "createdAt", "metadata");
            if (!DateTimeOffset.TryParseExact(
                    createdAtText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt) ||
                createdAt.Offset != TimeSpan.Zero)
            {
                throw new ImageMetadataValidationException("The metadata createdAt value must be a UTC timestamp.");
            }

            var baseImage = ReadFile(root["baseImage"], "baseImage");
            var ovmfVariables = ReadFile(root["ovmfVariablesTemplate"], "ovmfVariablesTemplate");
            var ovmfCode = ReadExternalFile(root["ovmfCode"]);
            var swtpmState = ReadTree(root["swtpmStateTemplate"]);
            var buildProvenance = schemaVersion == 1
                ? null
                : ReadBuildProvenance(root["buildProvenance"]);

            return new ManagedImageMetadata(
                schemaVersion,
                imageId,
                machineId,
                createdAt,
                baseImage,
                ovmfCode,
                ovmfVariables,
                swtpmState,
                buildProvenance);
        }
        catch (ImageMetadataValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ImageMetadataValidationException("The image metadata JSON is invalid.", exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new ImageMetadataValidationException("The image metadata contains an invalid value.", exception);
        }
    }

    private static void WriteFile(
        Utf8JsonWriter writer,
        string propertyName,
        ManagedImageFileMetadata metadata)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("sha256", metadata.Sha256);
        writer.WriteNumber("length", metadata.Length);
        writer.WriteNumber("lastWriteUtcTicks", metadata.LastWriteUtcTicks);
        writer.WriteEndObject();
    }

    private static ManagedImageFileMetadata ReadFile(JsonElement element, string context)
    {
        var properties = ReadStrictObject(element, FileProperties, context);
        return new ManagedImageFileMetadata(
            ReadSha256(properties, "sha256", context),
            ReadNonNegativeInt64(properties, "length", context),
            ReadUtcTicks(properties, "lastWriteUtcTicks", context));
    }

    private static ExternalImageFileMetadata ReadExternalFile(JsonElement element) =>
        ReadExternalFile(element, "ovmfCode");

    private static ExternalImageFileMetadata ReadExternalFile(JsonElement element, string context)
    {
        var properties = ReadStrictObject(element, ExternalFileProperties, context);
        var path = ReadString(properties, "path", context);
        if (path.Length > 4096 || !Path.IsPathFullyQualified(path) ||
            !string.Equals(path, Path.GetFullPath(path), StringComparison.Ordinal))
        {
            throw new ImageMetadataValidationException("The ovmfCode path must be a normalized absolute path.");
        }

        return new ExternalImageFileMetadata(
            path,
            ReadSha256(properties, "sha256", context),
            ReadNonNegativeInt64(properties, "length", context),
            ReadUtcTicks(properties, "lastWriteUtcTicks", context));
    }

    private static ManagedImageTreeMetadata ReadTree(JsonElement element)
    {
        const string context = "swtpmStateTemplate";
        var properties = ReadStrictObject(element, TreeProperties, context);
        var fileCount = ReadInt32(properties, "fileCount", context);
        if (fileCount < 0 || fileCount > ImageRegistryLimits.MaximumTpmFileCount)
        {
            throw new ImageMetadataValidationException("The swtpm fileCount is outside its permitted range.");
        }

        var totalLength = ReadNonNegativeInt64(properties, "totalLength", context);
        if (totalLength > ImageRegistryLimits.MaximumTpmTotalBytes)
        {
            throw new ImageMetadataValidationException("The swtpm totalLength exceeds its permitted limit.");
        }

        return new ManagedImageTreeMetadata(
            ReadSha256(properties, "sha256", context),
            fileCount,
            totalLength,
            ReadUtcTicks(properties, "lastWriteUtcTicks", context));
    }

    private static void WriteBuildProvenance(
        Utf8JsonWriter writer,
        ManagedImageBuildProvenance? provenance)
    {
        if (provenance is null)
        {
            writer.WriteNull("buildProvenance");
            return;
        }

        writer.WriteStartObject("buildProvenance");
        WriteExternalFile(writer, "windowsIso", provenance.WindowsIso);
        WriteExternalFile(writer, "virtioWinIso", provenance.VirtioWinIso);
        WriteExternalFile(writer, "guestBridgeExecutable", provenance.GuestBridgeExecutable);
        writer.WriteString("guestBridgeVersion", provenance.GuestBridgeVersion);
        writer.WriteString("windowsArchitecture", provenance.WindowsArchitecture);
        writer.WriteNumber("windowsBuild", provenance.WindowsBuild);
        writer.WriteString("windowsEditionId", provenance.WindowsEditionId);
        writer.WriteString("windowsDisplayVersion", provenance.WindowsDisplayVersion);
        writer.WriteNumber("diskSizeGiB", provenance.DiskSizeGiB);
        writer.WriteNumber("cpuCount", provenance.CpuCount);
        writer.WriteNumber("memoryMiB", provenance.MemoryMiB);
        writer.WriteString("evidenceKind", provenance.EvidenceKind);
        writer.WriteString(
            "observedAt",
            provenance.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteExternalFile(
        Utf8JsonWriter writer,
        string propertyName,
        ExternalImageFileMetadata metadata)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("path", metadata.Path);
        writer.WriteString("sha256", metadata.Sha256);
        writer.WriteNumber("length", metadata.Length);
        writer.WriteNumber("lastWriteUtcTicks", metadata.LastWriteUtcTicks);
        writer.WriteEndObject();
    }

    private static ManagedImageBuildProvenance? ReadBuildProvenance(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        const string context = "buildProvenance";
        var properties = ReadStrictObject(element, BuildProvenanceProperties, context);
        var windowsArchitecture = ReadBoundedText(
            properties,
            "windowsArchitecture",
            context,
            16);
        if (!string.Equals(windowsArchitecture, "X64", StringComparison.Ordinal))
        {
            throw new ImageMetadataValidationException(
                "The buildProvenance.windowsArchitecture value is unsupported.");
        }

        var windowsBuild = ReadInt32(properties, "windowsBuild", context);
        var diskSizeGiB = ReadInt32(properties, "diskSizeGiB", context);
        var cpuCount = ReadInt32(properties, "cpuCount", context);
        var memoryMiB = ReadInt32(properties, "memoryMiB", context);
        if (windowsBuild < 22000 ||
            diskSizeGiB is < 64 or > 1024 ||
            cpuCount is < 2 or > 32 ||
            memoryMiB is < 4096 or > 131072)
        {
            throw new ImageMetadataValidationException(
                "The buildProvenance numeric resource values are invalid.");
        }

        var evidenceKind = ReadBoundedText(properties, "evidenceKind", context, 64);
        if (!string.Equals(
                evidenceKind,
                ManagedImageBuildProvenance.GuestSelfReportEvidence,
                StringComparison.Ordinal))
        {
            throw new ImageMetadataValidationException(
                "The buildProvenance.evidenceKind value is unsupported.");
        }

        var observedAtText = ReadString(properties, "observedAt", context);
        if (!DateTimeOffset.TryParseExact(
                observedAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var observedAt) ||
            observedAt.Offset != TimeSpan.Zero)
        {
            throw new ImageMetadataValidationException(
                "The buildProvenance.observedAt value must be a UTC timestamp.");
        }

        return new ManagedImageBuildProvenance(
            ReadExternalFile(properties["windowsIso"], "buildProvenance.windowsIso"),
            ReadExternalFile(properties["virtioWinIso"], "buildProvenance.virtioWinIso"),
            ReadExternalFile(
                properties["guestBridgeExecutable"],
                "buildProvenance.guestBridgeExecutable"),
            ReadBoundedText(properties, "guestBridgeVersion", context, 128),
            windowsArchitecture,
            windowsBuild,
            ReadBoundedText(properties, "windowsEditionId", context, 128),
            ReadBoundedText(properties, "windowsDisplayVersion", context, 64),
            diskSizeGiB,
            cpuCount,
            memoryMiB,
            evidenceKind,
            observedAt);
    }

    private static Dictionary<string, JsonElement> ReadStrictObject(
        JsonElement element,
        IReadOnlyCollection<string> expectedProperties,
        string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ImageMetadataValidationException($"The {context} value must be an object.");
        }

        var expected = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new ImageMetadataValidationException(
                    $"The {context} object contains the unknown property '{property.Name}'.");
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new ImageMetadataValidationException(
                    $"The {context} object contains the duplicate property '{property.Name}'.");
            }
        }

        var missing = expected.FirstOrDefault(property => !properties.ContainsKey(property));
        if (missing is not null)
        {
            throw new ImageMetadataValidationException(
                $"The {context} object is missing the required property '{missing}'.");
        }

        return properties;
    }

    private static int ReadSchemaVersion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ImageMetadataValidationException("The metadata value must be an object.");
        }

        JsonElement schemaVersion = default;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
            {
                schemaVersion = property.Value;
                count++;
            }
        }

        if (count != 1 || !schemaVersion.TryGetInt32(out var value))
        {
            throw new ImageMetadataValidationException(
                "The metadata must contain exactly one integer schemaVersion.");
        }

        return value;
    }

    private static ImageId ParseImageId(string value)
    {
        try
        {
            return ImageId.Parse(value);
        }
        catch (FormatException exception)
        {
            throw new ImageMetadataValidationException("The metadata imageId is invalid.", exception);
        }
    }

    private static string ReadSha256(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context)
    {
        var value = ReadString(properties, propertyName, context);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} value must be a lowercase SHA-256 digest.");
        }

        return value;
    }

    private static long ReadUtcTicks(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context)
    {
        var value = ReadNonNegativeInt64(properties, propertyName, context);
        if (value > DateTime.MaxValue.Ticks)
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} value is outside the valid timestamp range.");
        }

        return value;
    }

    private static long ReadNonNegativeInt64(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context)
    {
        var element = properties[propertyName];
        if (!element.TryGetInt64(out var value) || value < 0)
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} value must be a non-negative integer.");
        }

        return value;
    }

    private static int ReadInt32(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context)
    {
        if (!properties[propertyName].TryGetInt32(out var value))
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} value must be an integer.");
        }

        return value;
    }

    private static string ReadString(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context)
    {
        var element = properties[propertyName];
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} value must be a string.");
        }

        return element.GetString()!;
    }

    private static string ReadBoundedText(
        Dictionary<string, JsonElement> properties,
        string propertyName,
        string context,
        int maximumLength)
    {
        var value = ReadString(properties, propertyName, context);
        if (value.Length is 0 || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ImageMetadataValidationException(
                $"The {context}.{propertyName} text is invalid.");
        }

        return value;
    }
}
