using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Application.Catalog;

internal sealed class CatalogAssetStore
{
    internal const int MaximumImageBytes = 4 * 1024 * 1024;
    internal const int MaximumImageCount = 1024;
    internal const long MaximumTotalImageBytes = 32L * 1024 * 1024;
    private const int MaximumManifestBytes = 512 * 1024;
    private const int MaximumImageDimension = 4096;
    private const int ManifestSchemaVersion = 1;
    private const string ManifestFileName = "assets.json";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly IReadOnlySet<string> RootManifestProperties = new HashSet<string>(
        ["schemaVersion", "catalogSha256", "upstreamRepository", "upstreamCommit", "images"],
        StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ImageManifestProperties = new HashSet<string>(
        ["serviceId", "category", "length", "sha256"],
        StringComparer.Ordinal);
    private readonly string _assetRoot;

    public CatalogAssetStore(string catalogDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        _assetRoot = Path.Combine(Path.GetFullPath(catalogDirectory), "assets");
    }

    public async Task<IReadOnlyDictionary<ServiceId, CatalogImageAsset>> EnsureAsync(
        CatalogSnapshot snapshot,
        string sourceImagesDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagesDirectory);

        EnsurePrivateDirectory(_assetRoot);
        var targetPath = GetSnapshotPath(snapshot);
        if (Directory.Exists(targetPath))
        {
            return await LoadRequiredAsync(snapshot, targetPath, cancellationToken).ConfigureAwait(false);
        }

        var sourceRoot = Path.GetFullPath(sourceImagesDirectory);
        EnsureRegularDirectory(sourceRoot, "catalog image source");
        var stagingPath = Path.Combine(_assetRoot, $".staging-{Guid.NewGuid():N}");
        EnsurePrivateDirectory(stagingPath);

        try
        {
            var entries = await StageImagesAsync(
                    snapshot,
                    sourceRoot,
                    stagingPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteManifestAsync(
                    stagingPath,
                    snapshot.Manifest.CatalogSha256,
                    snapshot.Manifest.UpstreamRepository,
                    snapshot.Manifest.UpstreamCommit,
                    entries,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                Directory.Move(stagingPath, targetPath);
            }
            catch (IOException) when (Directory.Exists(targetPath))
            {
                DeleteOwnedStaging(stagingPath);
            }

            return await LoadRequiredAsync(snapshot, targetPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteOwnedStaging(stagingPath);
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<ServiceId, CatalogImageAsset>> LoadAsync(
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var snapshotPath = GetSnapshotPath(snapshot);
        return Directory.Exists(snapshotPath)
            ? await LoadRequiredAsync(snapshot, snapshotPath, cancellationToken).ConfigureAwait(false)
            : EmptyAssets();
    }

    private static async Task<List<AssetManifestEntry>> StageImagesAsync(
        CatalogSnapshot snapshot,
        string sourceRoot,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var destinationImages = Path.Combine(stagingPath, "images");
        EnsurePrivateDirectory(destinationImages);
        var entries = new List<AssetManifestEntry>();
        long totalBytes = 0;

        foreach (var service in snapshot.Catalog.Services.OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumImageCount)
            {
                throw new CatalogWorkspaceException(
                    $"The catalog has more than {MaximumImageCount} image assets.");
            }

            var categoryName = service.Category.ToString();
            var sourceCategory = Path.Combine(sourceRoot, categoryName);
            if (!Directory.Exists(sourceCategory))
            {
                continue;
            }

            EnsureRegularDirectory(sourceCategory, $"catalog image category '{categoryName}'");
            var sourcePath = Path.Combine(sourceCategory, $"{service.Id.Value}.png");
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            EnsureRegularFile(sourcePath, $"catalog image for '{service.Id}'");
            var contents = await ReadFileBoundedAsync(
                    sourcePath,
                    MaximumImageBytes,
                    $"catalog image for '{service.Id}'",
                    cancellationToken)
                .ConfigureAwait(false);
            ValidatePng(contents, service.Id);
            totalBytes += contents.Length;
            if (totalBytes > MaximumTotalImageBytes)
            {
                throw new CatalogWorkspaceException(
                    $"Catalog images exceed the {MaximumTotalImageBytes}-byte total limit.");
            }

            var destinationCategory = Path.Combine(destinationImages, categoryName);
            EnsurePrivateDirectory(destinationCategory);
            var destinationPath = Path.Combine(destinationCategory, $"{service.Id.Value}.png");
            await WriteFileAsync(destinationPath, contents, cancellationToken).ConfigureAwait(false);
            entries.Add(new AssetManifestEntry(
                service.Id,
                service.Category,
                contents.LongLength,
                Convert.ToHexString(SHA256.HashData(contents))));
        }

        return entries;
    }

    private static async Task WriteManifestAsync(
        string stagingPath,
        string catalogSha256,
        string upstreamRepository,
        string upstreamCommit,
        IReadOnlyList<AssetManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", ManifestSchemaVersion);
            writer.WriteString("catalogSha256", catalogSha256);
            writer.WriteString("upstreamRepository", upstreamRepository);
            writer.WriteString("upstreamCommit", upstreamCommit);
            writer.WriteStartArray("images");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("serviceId", entry.ServiceId.Value);
                writer.WriteString("category", entry.Category.ToString());
                writer.WriteNumber("length", entry.Length);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (buffer.Length > MaximumManifestBytes)
        {
            throw new CatalogWorkspaceException(
                $"The catalog asset manifest exceeds its {MaximumManifestBytes}-byte limit.");
        }

        await WriteFileAsync(
                Path.Combine(stagingPath, ManifestFileName),
                buffer.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<ServiceId, CatalogImageAsset>> LoadRequiredAsync(
        CatalogSnapshot snapshot,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        EnsureRegularDirectory(snapshotPath, "catalog asset snapshot");
        var manifestPath = Path.Combine(snapshotPath, ManifestFileName);
        EnsureRegularFile(manifestPath, "catalog asset manifest");
        var manifestBytes = await ReadFileBoundedAsync(
                manifestPath,
                MaximumManifestBytes,
                "catalog asset manifest",
                cancellationToken)
            .ConfigureAwait(false);
        var entries = ParseManifest(manifestBytes, snapshot);
        ValidateSnapshotTree(snapshotPath, entries);

        var result = new Dictionary<ServiceId, CatalogImageAsset>();
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetImagePath(snapshotPath, entry);
            EnsureRegularFile(path, $"catalog image for '{entry.ServiceId}'");
            var info = new FileInfo(path);
            if (info.Length != entry.Length)
            {
                throw new CatalogWorkspaceException(
                    $"The catalog image for '{entry.ServiceId}' does not match its manifest length.");
            }

            totalBytes += info.Length;
            if (totalBytes > MaximumTotalImageBytes)
            {
                throw new CatalogWorkspaceException(
                    $"Catalog images exceed the {MaximumTotalImageBytes}-byte total limit.");
            }

            var contents = await ReadFileBoundedAsync(
                    path,
                    MaximumImageBytes,
                    $"catalog image for '{entry.ServiceId}'",
                    cancellationToken)
                .ConfigureAwait(false);
            ValidatePng(contents, entry.ServiceId);
            var hash = Convert.ToHexString(SHA256.HashData(contents));
            if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
            {
                throw new CatalogWorkspaceException(
                    $"The catalog image for '{entry.ServiceId}' does not match its manifest SHA-256 digest.");
            }

            result.Add(entry.ServiceId, new CatalogImageAsset(path, entry.Length, entry.Sha256));
        }

        return new ReadOnlyDictionary<ServiceId, CatalogImageAsset>(result);
    }

    private static List<AssetManifestEntry> ParseManifest(
        ReadOnlyMemory<byte> manifest,
        CatalogSnapshot snapshot)
    {
        try
        {
            using var json = JsonDocument.Parse(
                manifest,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var root = json.RootElement;
            EnsureObjectProperties(root, RootManifestProperties);
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var schema) ||
                schema != ManifestSchemaVersion ||
                !root.TryGetProperty("catalogSha256", out var catalogHash) ||
                catalogHash.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    catalogHash.GetString(),
                    snapshot.Manifest.CatalogSha256,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("upstreamRepository", out var upstreamRepository) ||
                upstreamRepository.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    upstreamRepository.GetString(),
                    snapshot.Manifest.UpstreamRepository,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("upstreamCommit", out var upstreamCommit) ||
                upstreamCommit.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    upstreamCommit.GetString(),
                    snapshot.Manifest.UpstreamCommit,
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogWorkspaceException("The catalog asset manifest is invalid.");
            }

            var services = snapshot.Catalog.Services.ToDictionary(item => item.Id);
            var entries = new List<AssetManifestEntry>();
            foreach (var image in images.EnumerateArray())
            {
                if (entries.Count >= MaximumImageCount)
                {
                    throw new CatalogWorkspaceException(
                        $"The catalog asset manifest has more than {MaximumImageCount} images.");
                }

                EnsureObjectProperties(image, ImageManifestProperties);
                var serviceIdText = RequiredString(image, "serviceId", 128);
                if (!ServiceId.TryCreate(serviceIdText, out var serviceId) ||
                    !services.TryGetValue(serviceId, out var service))
                {
                    throw new CatalogWorkspaceException(
                        $"The catalog asset manifest contains an unknown service '{serviceIdText}'.");
                }

                var categoryText = RequiredString(image, "category", 32);
                if (!Enum.TryParse<CatalogCategory>(categoryText, ignoreCase: false, out var category) ||
                    category != service.Category ||
                    !image.TryGetProperty("length", out var lengthProperty) ||
                    !lengthProperty.TryGetInt64(out var length) ||
                    length <= 0 ||
                    length > MaximumImageBytes)
                {
                    throw new CatalogWorkspaceException(
                        $"The catalog asset manifest entry for '{serviceId}' is invalid.");
                }

                var sha256 = RequiredString(image, "sha256", 64);
                if (!IsSha256(sha256) || entries.Any(item => item.ServiceId == serviceId))
                {
                    throw new CatalogWorkspaceException(
                        $"The catalog asset manifest entry for '{serviceId}' is invalid or duplicated.");
                }

                entries.Add(new AssetManifestEntry(
                    serviceId,
                    category,
                    length,
                    sha256.ToUpperInvariant()));
            }

            return entries;
        }
        catch (CatalogWorkspaceException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CatalogWorkspaceException("The catalog asset manifest JSON is invalid.", exception);
        }
    }

    private static void ValidateSnapshotTree(
        string snapshotPath,
        IReadOnlyList<AssetManifestEntry> entries)
    {
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            ManifestFileName,
        };
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal)
        {
            "images",
        };
        foreach (var entry in entries)
        {
            expectedDirectories.Add(Path.Combine("images", entry.Category.ToString()));
            expectedFiles.Add(Path.Combine(
                "images",
                entry.Category.ToString(),
                $"{entry.ServiceId.Value}.png"));
        }

        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(snapshotPath);
        var entryCount = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureRegularDirectory(directory, "catalog asset snapshot directory");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > MaximumImageCount * 3 + 16)
                {
                    throw new CatalogWorkspaceException("The catalog asset snapshot has too many entries.");
                }

                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new CatalogWorkspaceException(
                        $"The catalog asset snapshot contains a symbolic link: {path}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    actualDirectories.Add(Path.GetRelativePath(snapshotPath, path));
                    pending.Push(path);
                }
                else
                {
                    actualFiles.Add(Path.GetRelativePath(snapshotPath, path));
                }
            }
        }

        if (!actualFiles.SetEquals(expectedFiles) ||
            !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new CatalogWorkspaceException(
                "The catalog asset snapshot contains missing or unexpected files.");
        }
    }

    private static void ValidatePng(ReadOnlySpan<byte> contents, ServiceId serviceId)
    {
        if (contents.Length < 33 ||
            !contents[..PngSignature.Length].SequenceEqual(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(contents.Slice(8, 4)) != 13 ||
            !contents.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new CatalogWorkspaceException(
                $"The catalog image for '{serviceId}' is not a valid PNG image.");
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(contents.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(contents.Slice(20, 4));
        if (width is 0 or > MaximumImageDimension || height is 0 or > MaximumImageDimension)
        {
            throw new CatalogWorkspaceException(
                $"The catalog image for '{serviceId}' exceeds safe dimensions.");
        }
    }

    private static string RequiredString(JsonElement element, string name, int maximumLength)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Length > maximumLength)
        {
            throw new CatalogWorkspaceException(
                $"The catalog asset manifest property '{name}' is invalid.");
        }

        return property.GetString()!;
    }

    private static void EnsureObjectProperties(JsonElement element, IReadOnlySet<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogWorkspaceException("The catalog asset manifest is invalid.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new CatalogWorkspaceException(
                    $"The catalog asset manifest property '{property.Name}' is unknown or duplicated.");
            }
        }
    }

    private static async Task<byte[]> ReadFileBoundedAsync(
        string path,
        int limit,
        string description,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > limit)
        {
            throw new CatalogWorkspaceException($"The {description} exceeds its {limit}-byte limit.");
        }

        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var destination = new MemoryStream(capacity: (int)Math.Min(info.Length, limit));
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (destination.Length + count > limit)
                {
                    throw new CatalogWorkspaceException(
                        $"The {description} exceeds its {limit}-byte limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteFileAsync(
        string path,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }
    }

    private string GetSnapshotPath(CatalogSnapshot snapshot) =>
        Path.Combine(
            _assetRoot,
            $"{snapshot.Manifest.CatalogSha256}-{snapshot.Manifest.UpstreamCommit}");

    private static string GetImagePath(string snapshotPath, AssetManifestEntry entry) =>
        Path.Combine(
            snapshotPath,
            "images",
            entry.Category.ToString(),
            $"{entry.ServiceId.Value}.png");

    private static void EnsureRegularDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new CatalogWorkspaceException($"The {description} does not exist: {path}");
        }

        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogWorkspaceException($"The {description} must not be a symbolic link: {path}");
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new CatalogWorkspaceException($"The {description} does not exist: {path}");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogWorkspaceException($"The {description} must be a regular file: {path}");
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new CatalogWorkspaceException(
                $"A file exists where a catalog asset directory is required: {path}");
        }

        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogWorkspaceException(
                $"A catalog asset directory must not be a symbolic link: {path}");
        }

        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void DeleteOwnedStaging(string stagingPath)
    {
        if (!Directory.Exists(stagingPath))
        {
            return;
        }

        if (File.GetAttributes(stagingPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogWorkspaceException(
                $"Refusing to delete a symbolic-link staging directory: {stagingPath}");
        }

        Directory.Delete(stagingPath, recursive: true);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static ReadOnlyDictionary<ServiceId, CatalogImageAsset> EmptyAssets() =>
        new ReadOnlyDictionary<ServiceId, CatalogImageAsset>(
            new Dictionary<ServiceId, CatalogImageAsset>());

    private sealed record AssetManifestEntry(
        ServiceId ServiceId,
        CatalogCategory Category,
        long Length,
        string Sha256);
}
