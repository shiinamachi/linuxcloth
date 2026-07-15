using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace LinuxCloth.Wsb;

public static class GuestConfigStager
{
    private const int MaximumCatalogBytes = 16 * 1024 * 1024;

    public const string LaunchManifestFileName = "launch.json";
    public const string LaunchManifestHashFileName = "launch.json.sha256";
    public const string ExpressWsbFileName = "express.wsb";
    public const string CatalogFileName = "Catalog.xml";

    public static async Task StageAsync(
        string destinationDirectory,
        GuestLaunchManifest manifest,
        string expressWsb,
        string? catalogSnapshotPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expressWsb);

        var configuration = WsbParser.Parse(expressWsb, WsbParseMode.Normal);
        ValidateConfigurationMatchesManifest(configuration, manifest);

        if (catalogSnapshotPath is not null && !File.Exists(catalogSnapshotPath))
        {
            throw new FileNotFoundException("The catalog snapshot does not exist.", catalogSnapshotPath);
        }

        var fullDestination = Path.GetFullPath(destinationDirectory);
        if (string.Equals(fullDestination, Path.GetPathRoot(fullDestination), StringComparison.Ordinal))
        {
            throw new ArgumentException("The staging destination cannot be a filesystem root.", nameof(destinationDirectory));
        }

        if (Directory.Exists(fullDestination) || File.Exists(fullDestination))
        {
            throw new IOException("The staging destination already exists.");
        }

        var parentDirectory = Path.GetDirectoryName(fullDestination)
            ?? throw new ArgumentException("The staging destination must have a parent directory.", nameof(destinationDirectory));
        Directory.CreateDirectory(parentDirectory);

        var temporaryDirectory = Path.Combine(parentDirectory, $".linuxcloth-config-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        ApplyPrivateDirectoryMode(temporaryDirectory);
        var published = false;

        try
        {
            var manifestBytes = GuestLaunchManifestSerializer.SerializeToUtf8Bytes(manifest);
            var manifestHash = GuestLaunchManifestSerializer.ComputeSha256Hex(manifestBytes);
            var hashSidecar = Encoding.ASCII.GetBytes($"{manifestHash}  {LaunchManifestFileName}\n");
            var wsbBytes = WsbSerializer.SerializeToUtf8Bytes(configuration);

            await WriteFileAsync(
                Path.Combine(temporaryDirectory, LaunchManifestFileName),
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(
                Path.Combine(temporaryDirectory, LaunchManifestHashFileName),
                hashSidecar,
                cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(
                Path.Combine(temporaryDirectory, ExpressWsbFileName),
                wsbBytes,
                cancellationToken).ConfigureAwait(false);

            if (catalogSnapshotPath is not null)
            {
                if (new FileInfo(catalogSnapshotPath).Length > MaximumCatalogBytes)
                {
                    throw new InvalidDataException($"Catalog.xml exceeds the {MaximumCatalogBytes}-byte staging limit.");
                }

                var copiedCatalogHash = await CopyFileAndHashAsync(
                    catalogSnapshotPath,
                    Path.Combine(temporaryDirectory, CatalogFileName),
                    cancellationToken).ConfigureAwait(false);

                if (!string.Equals(copiedCatalogHash, manifest.CatalogSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The copied Catalog.xml does not match the manifest SHA-256.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryDirectory, fullDestination);
            published = true;
        }
        finally
        {
            if (!published && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void ValidateConfigurationMatchesManifest(
        WsbConfiguration configuration,
        GuestLaunchManifest manifest)
    {
        if (configuration.ExpressServiceIds is null ||
            !configuration.ExpressServiceIds.SequenceEqual(manifest.ServiceIds))
        {
            throw new InvalidDataException("The Express WSB service identifiers do not match launch.json.");
        }

        var expectedNetwork = manifest.NetworkEnabled ? WsbFeatureState.Enable : WsbFeatureState.Disable;
        var expectedClipboard = manifest.ClipboardEnabled ? WsbFeatureState.Enable : WsbFeatureState.Disable;

        if (configuration.Networking != expectedNetwork ||
            configuration.ClipboardRedirection != expectedClipboard ||
            configuration.VirtualGpu != WsbFeatureState.Disable ||
            configuration.MappedFolders.Count != 0)
        {
            throw new InvalidDataException("The Express WSB security flags do not match launch.json.");
        }
    }

    private static async Task WriteFileAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        ApplyPrivateFileMode(path);
    }

    private static async Task<string> CopyFileAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (source.Position > MaximumCatalogBytes)
                {
                    throw new InvalidDataException($"Catalog.xml exceeds the {MaximumCatalogBytes}-byte staging limit.");
                }

                hash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            ApplyPrivateFileMode(destinationPath);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ApplyPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ApplyPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
