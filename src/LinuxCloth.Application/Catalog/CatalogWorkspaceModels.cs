using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Application.Catalog;

public sealed record CatalogImageAsset(
    string Path,
    long Length,
    string Sha256);

public sealed record CatalogServiceEntry(
    CatalogService Service,
    CompatibilityRecord Compatibility,
    CatalogImageAsset? Image);

public sealed class CatalogWorkspaceState
{
    private readonly CatalogIndex _index;
    private readonly ReadOnlyDictionary<ServiceId, CatalogServiceEntry> _servicesById;

    internal CatalogWorkspaceState(
        CatalogSnapshot snapshot,
        CompatibilityOverlay compatibility,
        IReadOnlyDictionary<ServiceId, CatalogImageAsset> images)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(images);

        Snapshot = snapshot;
        _index = new CatalogIndex(snapshot.Catalog);
        Categories = _index.Categories;

        var composed = CatalogComposer.Compose(snapshot.Catalog, compatibility);
        var entries = new Dictionary<ServiceId, CatalogServiceEntry>();
        foreach (var item in composed)
        {
            images.TryGetValue(item.Service.Id, out var image);
            entries.Add(
                item.Service.Id,
                new CatalogServiceEntry(item.Service, item.Compatibility, image));
        }

        _servicesById = new ReadOnlyDictionary<ServiceId, CatalogServiceEntry>(entries);
        Services = Array.AsReadOnly(
            _index.All.Select(service => _servicesById[service.Id]).ToArray());
    }

    public CatalogSnapshot Snapshot { get; }

    public IReadOnlyList<CatalogDiagnostic> Diagnostics => Snapshot.Catalog.Diagnostics;

    public IReadOnlyList<CatalogCategory> Categories { get; }

    public IReadOnlyList<CatalogServiceEntry> Services { get; }

    public IReadOnlyList<CatalogServiceEntry> Search(
        string? query,
        CatalogCategory? category = null) =>
        _index.Search(query, category)
            .Select(service => _servicesById[service.Id])
            .ToArray();

    public IReadOnlyList<CatalogServiceEntry> GetByCategory(CatalogCategory category) =>
        _index.GetByCategory(category)
            .Select(service => _servicesById[service.Id])
            .ToArray();

    public bool TryGetService(
        ServiceId serviceId,
        [NotNullWhen(true)] out CatalogServiceEntry? service) =>
        _servicesById.TryGetValue(serviceId, out service);
}

public sealed class CatalogWorkspaceException : Exception
{
    public CatalogWorkspaceException(string message)
        : base(message)
    {
    }

    public CatalogWorkspaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
