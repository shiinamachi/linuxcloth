using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinuxCloth.Catalog;

public sealed record CatalogSnapshotManifest
{
    public const int CurrentSchemaVersion = 1;

    public CatalogSnapshotManifest(
        int schemaVersion,
        string upstreamRepository,
        string upstreamCommit,
        string catalogSha256,
        DateTimeOffset retrievedAt)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new CatalogValidationException(
                $"Snapshot schema version {schemaVersion} is not supported.");
        }

        if (string.IsNullOrWhiteSpace(upstreamRepository))
        {
            throw new CatalogValidationException("The upstream repository is required.");
        }

        if (string.IsNullOrWhiteSpace(upstreamCommit))
        {
            throw new CatalogValidationException("The upstream commit is required.");
        }

        if (!IsSha256(catalogSha256))
        {
            throw new CatalogValidationException("catalogSha256 must be a 64-character SHA-256 digest.");
        }

        SchemaVersion = schemaVersion;
        UpstreamRepository = upstreamRepository;
        UpstreamCommit = upstreamCommit;
        CatalogSha256 = catalogSha256.ToUpperInvariant();
        RetrievedAt = retrievedAt.ToUniversalTime();
    }

    public int SchemaVersion { get; }

    public string UpstreamRepository { get; }

    public string UpstreamCommit { get; }

    public string CatalogSha256 { get; }

    public DateTimeOffset RetrievedAt { get; }

    public static string ComputeCatalogSha256(ReadOnlySpan<byte> catalogXml) =>
        Convert.ToHexString(SHA256.HashData(catalogXml));

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("upstreamRepository", UpstreamRepository);
            writer.WriteString("upstreamCommit", UpstreamCommit);
            writer.WriteString("catalogSha256", CatalogSha256);
            writer.WriteString("retrievedAt", RetrievedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static CatalogSnapshotManifest Parse(ReadOnlyMemory<byte> json)
    {
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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var schema) ||
                !root.TryGetProperty("upstreamRepository", out var repository) ||
                repository.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("upstreamCommit", out var commit) ||
                commit.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("catalogSha256", out var sha256) ||
                sha256.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("retrievedAt", out var retrievedAt) ||
                retrievedAt.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParseExact(
                    retrievedAt.GetString(),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var retrieved))
            {
                throw new CatalogValidationException("The snapshot manifest is invalid.");
            }

            return new CatalogSnapshotManifest(
                schema,
                repository.GetString()!,
                commit.GetString()!,
                sha256.GetString()!,
                retrieved);
        }
        catch (CatalogValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CatalogValidationException("The snapshot manifest JSON is invalid.", exception);
        }
    }

    internal static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
