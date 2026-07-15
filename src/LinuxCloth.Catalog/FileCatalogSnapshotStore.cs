using System.Text;

namespace LinuxCloth.Catalog;

public sealed class FileCatalogSnapshotStore : ICatalogSnapshotStore, IDisposable
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumPointerBytes = 128;
    private const string CatalogFileName = "Catalog.xml";
    private const string ManifestFileName = "manifest.json";
    private const string CurrentPointerName = "current";
    private const string PreviousPointerName = "previous";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CatalogParser _parser;
    private readonly string _rootPath;
    private readonly string _snapshotsPath;

    public FileCatalogSnapshotStore(string rootPath, CatalogParser parser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(parser);

        _rootPath = Path.GetFullPath(rootPath);
        _snapshotsPath = Path.Combine(_rootPath, "snapshots");
        _parser = parser;
    }

    public async Task<CatalogSnapshot?> LoadLastKnownGoodAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? currentFailure = null;
            foreach (var pointerName in new[] { CurrentPointerName, PreviousPointerName })
            {
                try
                {
                    var hash = await ReadPointerAsync(pointerName, cancellationToken).ConfigureAwait(false);
                    if (hash is not null)
                    {
                        return await LoadSnapshotAsync(hash, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (CatalogValidationException exception)
                {
                    currentFailure ??= exception;
                }
                catch (IOException exception)
                {
                    currentFailure ??= exception;
                }
            }

            if (currentFailure is not null)
            {
                throw new CatalogValidationException(
                    "No valid last-known-good catalog snapshot could be loaded.",
                    currentFailure);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PromoteAsync(
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Verify();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_snapshotsPath);

            var hash = snapshot.Manifest.CatalogSha256;
            var targetPath = Path.Combine(_snapshotsPath, hash);
            if (!Directory.Exists(targetPath))
            {
                await StageSnapshotAsync(snapshot, targetPath, cancellationToken).ConfigureAwait(false);
            }

            _ = await LoadSnapshotAsync(hash, cancellationToken).ConfigureAwait(false);

            var previousHash = await ReadPointerAsync(CurrentPointerName, cancellationToken)
                .ConfigureAwait(false);
            if (previousHash is not null &&
                !string.Equals(previousHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _ = await LoadSnapshotAsync(previousHash, cancellationToken).ConfigureAwait(false);
                    await WritePointerAtomicallyAsync(
                            PreviousPointerName,
                            previousHash,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CatalogValidationException)
                {
                    // Preserve the existing previous pointer when current is corrupt.
                }
                catch (IOException)
                {
                    // Preserve the existing previous pointer when current cannot be read.
                }
            }

            await WritePointerAtomicallyAsync(CurrentPointerName, hash, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task StageSnapshotAsync(
        CatalogSnapshot snapshot,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(_snapshotsPath, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);

        try
        {
            await WriteFileAsync(
                    Path.Combine(stagingPath, CatalogFileName),
                    snapshot.CatalogXml,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteFileAsync(
                    Path.Combine(stagingPath, ManifestFileName),
                    snapshot.Manifest.ToJson(),
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                Directory.Move(stagingPath, targetPath);
            }
            catch (IOException) when (Directory.Exists(targetPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    private async Task<CatalogSnapshot> LoadSnapshotAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var snapshotPath = Path.Combine(_snapshotsPath, hash);
        var manifestPath = Path.Combine(snapshotPath, ManifestFileName);
        var catalogPath = Path.Combine(snapshotPath, CatalogFileName);

        EnsureFileWithinLimit(manifestPath, MaximumManifestBytes, "snapshot manifest");
        EnsureFileWithinLimit(catalogPath, CatalogParser.MaximumDocumentBytes, "catalog");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = CatalogSnapshotManifest.Parse(manifestBytes);
        if (!string.Equals(hash, manifest.CatalogSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogValidationException(
                "The snapshot pointer does not match the manifest SHA-256 digest.");
        }

        var catalogBytes = await File.ReadAllBytesAsync(catalogPath, cancellationToken)
            .ConfigureAwait(false);
        return CatalogSnapshot.FromPersisted(catalogBytes, manifest, _parser);
    }

    private async Task<string?> ReadPointerAsync(
        string pointerName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_rootPath, pointerName);
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureFileWithinLimit(path, MaximumPointerBytes, $"{pointerName} pointer");
        var value = (await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false))
            .Trim();
        ValidateHash(value);
        return value.ToUpperInvariant();
    }

    private async Task WritePointerAtomicallyAsync(
        string pointerName,
        string hash,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        Directory.CreateDirectory(_rootPath);

        var targetPath = Path.Combine(_rootPath, pointerName);
        var temporaryPath = Path.Combine(_rootPath, $".{pointerName}-{Guid.NewGuid():N}");
        try
        {
            await WriteFileAsync(
                    temporaryPath,
                    Encoding.ASCII.GetBytes(hash.ToUpperInvariant() + "\n"),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
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
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureFileWithinLimit(string path, int limit, string description)
    {
        var length = new FileInfo(path).Length;
        if (length > limit)
        {
            throw new CatalogValidationException(
                $"The persisted {description} exceeds its {limit}-byte limit.");
        }
    }

    private static void ValidateHash(string hash)
    {
        if (!CatalogSnapshotManifest.IsSha256(hash))
        {
            throw new CatalogValidationException("A snapshot pointer contains an invalid SHA-256 digest.");
        }
    }
}
