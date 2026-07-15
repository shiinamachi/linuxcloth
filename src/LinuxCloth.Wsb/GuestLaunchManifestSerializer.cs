using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

public static class GuestLaunchManifestSerializer
{
    public const int MaximumManifestBytes = 65_536;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly HashSet<string> KnownProperties =
        new(StringComparer.Ordinal)
        {
            "schemaVersion",
            "sessionId",
            "serviceIds",
            "catalogSha256",
            "networkEnabled",
            "clipboardEnabled",
            "issuedAt",
        };

    public static byte[] SerializeToUtf8Bytes(GuestLaunchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
            writer.WriteString("sessionId", manifest.SessionId.ToString("D"));
            writer.WriteStartArray("serviceIds");
            foreach (var serviceId in manifest.ServiceIds)
            {
                writer.WriteStringValue(serviceId.Value);
            }

            writer.WriteEndArray();
            writer.WriteString("catalogSha256", manifest.CatalogSha256);
            writer.WriteBoolean("networkEnabled", manifest.NetworkEnabled);
            writer.WriteBoolean("clipboardEnabled", manifest.ClipboardEnabled);
            writer.WriteString("issuedAt", manifest.IssuedAtUtc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return output.WrittenSpan.ToArray();
    }

    public static string Serialize(GuestLaunchManifest manifest) =>
        Encoding.UTF8.GetString(SerializeToUtf8Bytes(manifest));

    public static GuestLaunchManifest Deserialize(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaximumManifestBytes)
        {
            throw new LaunchManifestValidationException("The launch manifest exceeds the size limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LaunchManifestValidationException("The launch manifest root must be an object.");
            }

            ValidateProperties(root);

            if (!root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) ||
                schemaVersion != GuestLaunchManifest.CurrentSchemaVersion)
            {
                throw new LaunchManifestValidationException("The launch manifest schema version is unsupported.");
            }

            var sessionIdText = ReadRequiredString(root, "sessionId");
            if (!Guid.TryParseExact(sessionIdText, "D", out var sessionId) || sessionId == Guid.Empty)
            {
                throw new LaunchManifestValidationException("The launch manifest sessionId is not a non-empty UUID.");
            }

            var serviceIdsElement = root.GetProperty("serviceIds");
            if (serviceIdsElement.ValueKind != JsonValueKind.Array)
            {
                throw new LaunchManifestValidationException("The launch manifest serviceIds value must be an array.");
            }

            var serviceIds = new List<ServiceId>();
            foreach (var serviceIdElement in serviceIdsElement.EnumerateArray())
            {
                if (serviceIdElement.ValueKind != JsonValueKind.String ||
                    !ServiceId.TryCreate(serviceIdElement.GetString(), out var serviceId))
                {
                    throw new LaunchManifestValidationException("The launch manifest contains an invalid service identifier.");
                }

                serviceIds.Add(serviceId);
            }

            var networkElement = root.GetProperty("networkEnabled");
            var clipboardElement = root.GetProperty("clipboardEnabled");
            if (networkElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                clipboardElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new LaunchManifestValidationException("The launch manifest feature flags must be Boolean values.");
            }

            var issuedAtText = ReadRequiredString(root, "issuedAt");
            if (!DateTimeOffset.TryParseExact(
                    issuedAtText,
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var issuedAt))
            {
                throw new LaunchManifestValidationException("The launch manifest issuedAt value is not canonical UTC time.");
            }

            try
            {
                return new GuestLaunchManifest(
                    sessionId,
                    serviceIds,
                    ReadRequiredString(root, "catalogSha256"),
                    networkElement.GetBoolean(),
                    clipboardElement.GetBoolean(),
                    issuedAt);
            }
            catch (ArgumentException exception)
            {
                throw new LaunchManifestValidationException("The launch manifest contains an invalid value.", exception);
            }
        }
        catch (JsonException exception)
        {
            throw new LaunchManifestValidationException("The launch manifest is not valid JSON.", exception);
        }
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidateProperties(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!KnownProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new LaunchManifestValidationException("The launch manifest contains an unknown or duplicate property.");
            }
        }

        if (seen.Count != KnownProperties.Count)
        {
            throw new LaunchManifestValidationException("The launch manifest is missing a required property.");
        }
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        var element = root.GetProperty(propertyName);
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value)
        {
            throw new LaunchManifestValidationException($"The launch manifest {propertyName} value must be a string.");
        }

        return value;
    }
}
