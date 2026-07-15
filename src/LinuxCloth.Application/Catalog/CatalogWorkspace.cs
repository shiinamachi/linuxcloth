using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using LinuxCloth.Application.Launching;
using LinuxCloth.Application.Storage;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Application.Catalog;

public sealed class CatalogWorkspace : ILaunchCatalogResolver, IDisposable
{
    private const string CompatibilityFileName = "current.json";
    private readonly CatalogAssetStore _assetStore;
    private readonly OfficialCatalogBundle _bundledCatalog;
    private readonly CompatibilityOverlayParser _compatibilityParser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinuxClothPaths _paths;
    private readonly CatalogParser _parser = new(CatalogDuplicateIdPolicy.KeepFirst);
    private readonly FileCatalogSnapshotStore _store;
    private readonly TimeProvider _timeProvider;
    private IReadOnlyDictionary<ServiceId, CatalogImageAsset> _images = EmptyAssets();
    private CompatibilityOverlay _overlay = new([]);
    private CatalogWorkspaceState? _state;
    private bool _disposed;

    public CatalogWorkspace(
        LinuxClothPaths paths,
        OfficialCatalogBundle bundledCatalog,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(bundledCatalog);

        _paths = paths;
        _bundledCatalog = bundledCatalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = new FileCatalogSnapshotStore(paths.CatalogDirectory, _parser);
        _assetStore = new CatalogAssetStore(paths.CatalogDirectory);
    }

    public CatalogWorkspaceState Current =>
        Volatile.Read(ref _state) ??
        throw new InvalidOperationException("The catalog workspace has not been initialized.");

    public IReadOnlyList<CatalogCategory> Categories => Current.Categories;

    public IReadOnlyList<CatalogServiceEntry> Services => Current.Services;

    public Task<CatalogWorkspaceState> InitializeAsync(
        CancellationToken cancellationToken = default) => LoadAsync(cancellationToken);

    public async Task<CatalogWorkspaceState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.CreateBaseDirectories();
            var snapshot = await _store.LoadLastKnownGoodAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                var bundled = await CreateSnapshotAsync(_bundledCatalog, cancellationToken)
                    .ConfigureAwait(false);
                _ = await _assetStore.EnsureAsync(
                        bundled,
                        _bundledCatalog.ImagesDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.PromoteAsync(bundled, cancellationToken).ConfigureAwait(false);
                snapshot = await LoadPromotedSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            var images = await LoadOrHydrateImagesAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            var overlay = await LoadCompatibilityAsync(cancellationToken).ConfigureAwait(false);
            return Publish(snapshot, overlay, images);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CatalogWorkspaceState> PromoteBundleAsync(
        OfficialCatalogBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.CreateBaseDirectories();
            var candidate = await CreateSnapshotAsync(bundle, cancellationToken).ConfigureAwait(false);
            var existing = await _store.LoadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null &&
                string.Equals(
                    existing.Manifest.CatalogSha256,
                    candidate.Manifest.CatalogSha256,
                    StringComparison.Ordinal) &&
                (!string.Equals(
                     existing.Manifest.UpstreamRepository,
                     candidate.Manifest.UpstreamRepository,
                     StringComparison.Ordinal) ||
                 !string.Equals(
                     existing.Manifest.UpstreamCommit,
                     candidate.Manifest.UpstreamCommit,
                     StringComparison.Ordinal)))
            {
                throw new CatalogWorkspaceException(
                    "A catalog with identical XML bytes is already pinned to different upstream provenance.");
            }

            _ = await _assetStore.EnsureAsync(
                    candidate,
                    bundle.ImagesDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            await _store.PromoteAsync(candidate, cancellationToken).ConfigureAwait(false);

            var promoted = await LoadPromotedSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var images = await _assetStore.LoadAsync(promoted, cancellationToken).ConfigureAwait(false);
            var overlay = _state is null
                ? await LoadCompatibilityAsync(cancellationToken).ConfigureAwait(false)
                : _overlay;
            return Publish(promoted, overlay, images);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CatalogWorkspaceState> ApplyCompatibilityOverlayAsync(
        ReadOnlyMemory<byte> overlayJson,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var overlay = _compatibilityParser.Parse(overlayJson);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current;
            _paths.CreateBaseDirectories();
            await WriteCompatibilityAtomicallyAsync(overlayJson, cancellationToken)
                .ConfigureAwait(false);
            return Publish(current.Snapshot, overlay, _images);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CatalogWorkspaceState> ReloadCompatibilityOverlayAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current;
            var overlay = await LoadCompatibilityAsync(cancellationToken).ConfigureAwait(false);
            return Publish(current.Snapshot, overlay, _images);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<CatalogServiceEntry> Search(
        string? query,
        CatalogCategory? category = null) => Current.Search(query, category);

    public IReadOnlyList<CatalogServiceEntry> GetByCategory(CatalogCategory category) =>
        Current.GetByCategory(category);

    public bool TryGetService(
        ServiceId serviceId,
        [NotNullWhen(true)] out CatalogServiceEntry? service) =>
        Current.TryGetService(serviceId, out service);

    public Task<LaunchCatalogResolution> ResolveAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        cancellationToken.ThrowIfCancellationRequested();
        var state = Current;
        var resolvedIds = serviceIds.ToArray();
        if (resolvedIds.Length == 0 ||
            resolvedIds.Length > 32 ||
            resolvedIds.Distinct().Count() != resolvedIds.Length)
        {
            throw new ArgumentException(
                "Between one and 32 distinct catalog service identifiers are required.",
                nameof(serviceIds));
        }

        var displayNames = new string[resolvedIds.Length];
        for (var index = 0; index < resolvedIds.Length; index++)
        {
            if (!state.TryGetService(resolvedIds[index], out var service))
            {
                throw new KeyNotFoundException(
                    $"The service identifier '{resolvedIds[index]}' does not exist in the active catalog snapshot.");
            }

            displayNames[index] = service.Service.DisplayName;
        }

        var catalogPath = Path.Combine(
            _paths.CatalogDirectory,
            "snapshots",
            state.Snapshot.Manifest.CatalogSha256,
            "Catalog.xml");
        EnsureRegularFile(catalogPath, "active catalog snapshot");
        return Task.FromResult(new LaunchCatalogResolution(
            resolvedIds,
            catalogPath,
            state.Snapshot.Manifest.CatalogSha256,
            displayNames));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<IReadOnlyDictionary<ServiceId, CatalogImageAsset>> LoadOrHydrateImagesAsync(
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var images = await _assetStore.LoadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (images.Count > 0)
        {
            return images;
        }

        var bundled = await CreateSnapshotAsync(_bundledCatalog, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                bundled.Manifest.CatalogSha256,
                snapshot.Manifest.CatalogSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                bundled.Manifest.UpstreamRepository,
                snapshot.Manifest.UpstreamRepository,
                StringComparison.Ordinal) ||
            !string.Equals(
                bundled.Manifest.UpstreamCommit,
                snapshot.Manifest.UpstreamCommit,
                StringComparison.Ordinal))
        {
            return images;
        }

        return await _assetStore.EnsureAsync(
                bundled,
                _bundledCatalog.ImagesDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CatalogSnapshot> CreateSnapshotAsync(
        OfficialCatalogBundle bundle,
        CancellationToken cancellationToken)
    {
        EnsureRegularFile(bundle.CatalogPath, "official catalog bundle");
        var catalogXml = await ReadFileBoundedAsync(
                bundle.CatalogPath,
                CatalogParser.MaximumDocumentBytes,
                "official catalog bundle",
                cancellationToken)
            .ConfigureAwait(false);
        return CatalogSnapshot.Create(
            catalogXml,
            _parser,
            bundle.UpstreamRepository,
            bundle.UpstreamCommit,
            _timeProvider.GetUtcNow());
    }

    private async Task<CompatibilityOverlay> LoadCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.CompatibilityDirectory, CompatibilityFileName);
        if (!File.Exists(path))
        {
            return new CompatibilityOverlay([]);
        }

        EnsureRegularFile(path, "Linux compatibility overlay");
        var bytes = await ReadFileBoundedAsync(
                path,
                CompatibilityOverlayParser.MaximumDocumentBytes,
                "Linux compatibility overlay",
                cancellationToken)
            .ConfigureAwait(false);
        return _compatibilityParser.Parse(bytes);
    }

    private async Task WriteCompatibilityAtomicallyAsync(
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(_paths.CompatibilityDirectory, CompatibilityFileName);
        var temporaryPath = Path.Combine(
            _paths.CompatibilityDirectory,
            $".{CompatibilityFileName}-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private CatalogWorkspaceState Publish(
        CatalogSnapshot snapshot,
        CompatibilityOverlay overlay,
        IReadOnlyDictionary<ServiceId, CatalogImageAsset> images)
    {
        var state = new CatalogWorkspaceState(snapshot, overlay, images);
        _overlay = overlay;
        _images = images;
        Volatile.Write(ref _state, state);
        return state;
    }

    private async Task<CatalogSnapshot> LoadPromotedSnapshotAsync(
        CancellationToken cancellationToken) =>
        await _store.LoadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false) ??
        throw new CatalogWorkspaceException("The promoted catalog snapshot could not be loaded.");

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

    private static ReadOnlyDictionary<ServiceId, CatalogImageAsset> EmptyAssets() =>
        new ReadOnlyDictionary<ServiceId, CatalogImageAsset>(
            new Dictionary<ServiceId, CatalogImageAsset>());

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
