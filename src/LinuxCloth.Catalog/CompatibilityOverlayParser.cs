using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using LinuxCloth.Core;

namespace LinuxCloth.Catalog;

public sealed class CompatibilityOverlayParser
{
    public const int MaximumDocumentBytes = 1024 * 1024;
    private readonly int _maximumDocumentBytes = MaximumDocumentBytes;

    public CompatibilityOverlay Parse(ReadOnlyMemory<byte> document)
    {
        using var stream = new MemoryStream(document.ToArray(), writable: false);
        return Parse(stream);
    }

    public CompatibilityOverlay Parse(Stream document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.CanSeek && document.Length - document.Position > _maximumDocumentBytes)
        {
            throw new CatalogValidationException(
                $"The compatibility overlay exceeds the {_maximumDocumentBytes}-byte limit.");
        }

        try
        {
            using var bounded = new BoundedReadStream(document, _maximumDocumentBytes);
            using var json = JsonDocument.Parse(
                bounded,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            return ParseDocument(json.RootElement);
        }
        catch (CatalogValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException(
                "The compatibility overlay JSON is invalid.",
                exception);
        }
    }

    private static CompatibilityOverlay ParseDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogValidationException("The compatibility overlay must be a JSON object.");
        }

        if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !schemaVersion.TryGetInt32(out var schema) ||
            schema != 1)
        {
            throw new CatalogValidationException("The compatibility overlay schemaVersion must be 1.");
        }

        if (!root.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Array)
        {
            throw new CatalogValidationException("The compatibility overlay must contain a services array.");
        }

        var records = new List<CompatibilityRecord>();
        foreach (var service in services.EnumerateArray())
        {
            records.Add(ParseRecord(service));
        }

        return new CompatibilityOverlay(records);
    }

    private static CompatibilityRecord ParseRecord(JsonElement service)
    {
        if (service.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogValidationException("Each compatibility service must be an object.");
        }

        var serviceIdText = RequiredString(service, "serviceId");
        if (!ServiceId.TryCreate(serviceIdText, out var serviceId))
        {
            throw new CatalogValidationException(
                $"The compatibility service identifier '{serviceIdText}' is invalid.");
        }

        var status = RequiredString(service, "status") switch
        {
            "untested" => CompatibilityStatus.Untested,
            "verified" => CompatibilityStatus.Verified,
            "partial" => CompatibilityStatus.Partial,
            "blocked" => CompatibilityStatus.Blocked,
            var value => throw new CatalogValidationException(
                $"Compatibility status '{value}' is not supported."),
        };

        var preferredDisplay = OptionalString(service, "preferredDisplay") switch
        {
            null or "spice" => DisplayMode.Spice,
            "rdp" => DisplayMode.Rdp,
            "qemu-console" => DisplayMode.QemuConsole,
            var value => throw new CatalogValidationException(
                $"Preferred display '{value}' is not supported."),
        };

        return new CompatibilityRecord(
            serviceId,
            status,
            preferredDisplay,
            OptionalString(service, "testedWindowsBuild"),
            OptionalString(service, "testedSporkVersion"),
            OptionalString(service, "testedCatalogCommit"),
            OptionalString(service, "testedQemuVersion"),
            ParseKnownIssues(service),
            ParseDate(service, "lastVerifiedAt"));
    }

    private static ReadOnlyCollection<string> ParseKnownIssues(JsonElement service)
    {
        if (!service.TryGetProperty("knownIssues", out var issues) ||
            issues.ValueKind == JsonValueKind.Null)
        {
            return Array.AsReadOnly(Array.Empty<string>());
        }

        if (issues.ValueKind != JsonValueKind.Array)
        {
            throw new CatalogValidationException("knownIssues must be an array of strings.");
        }

        var result = new List<string>();
        foreach (var issue in issues.EnumerateArray())
        {
            if (issue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(issue.GetString()))
            {
                throw new CatalogValidationException("knownIssues must contain non-empty strings.");
            }

            result.Add(issue.GetString()!);
        }

        return result.AsReadOnly();
    }

    private static DateOnly? ParseDate(JsonElement service, string propertyName)
    {
        var value = OptionalString(service, propertyName);
        if (value is null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new CatalogValidationException(
                $"{propertyName} must use the yyyy-MM-dd format.");
        }

        return date;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) ??
        throw new CatalogValidationException($"{propertyName} is required.");

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new CatalogValidationException($"{propertyName} must be a non-empty string.");
        }

        return property.GetString();
    }
}
