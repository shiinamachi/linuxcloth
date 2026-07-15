using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using LinuxCloth.Core;

namespace LinuxCloth.Catalog;

public sealed class CompatibilityOverlay
{
    private readonly ReadOnlyDictionary<ServiceId, CompatibilityRecord> _records;
    private readonly IReadOnlyCollection<CompatibilityRecord> _recordList;

    public CompatibilityOverlay(IEnumerable<CompatibilityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var dictionary = new Dictionary<ServiceId, CompatibilityRecord>();
        foreach (var record in records)
        {
            if (!dictionary.TryAdd(record.ServiceId, record))
            {
                throw new CatalogValidationException(
                    $"Compatibility for service '{record.ServiceId}' is duplicated.");
            }
        }

        _records = new ReadOnlyDictionary<ServiceId, CompatibilityRecord>(dictionary);
        _recordList = Array.AsReadOnly(dictionary.Values.ToArray());
    }

    public IReadOnlyCollection<CompatibilityRecord> Records => _recordList;

    public bool TryGet(
        ServiceId serviceId,
        [NotNullWhen(true)] out CompatibilityRecord? record) =>
        _records.TryGetValue(serviceId, out record);
}

public sealed record CatalogServiceCompatibility(
    CatalogService Service,
    CompatibilityRecord Compatibility);

public static class CatalogComposer
{
    public static IReadOnlyList<CatalogServiceCompatibility> Compose(
        CatalogDocument catalog,
        CompatibilityOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(overlay);

        return new CatalogIndex(catalog).All
            .Select(service => new CatalogServiceCompatibility(
                service,
                overlay.TryGet(service.Id, out var compatibility)
                    ? compatibility
                    : CreateUntested(service.Id)))
            .ToArray();
    }

    private static CompatibilityRecord CreateUntested(ServiceId serviceId) =>
        new(
            serviceId,
            CompatibilityStatus.Untested,
            DisplayMode.Spice,
            TestedWindowsBuild: null,
            TestedSporkVersion: null,
            TestedCatalogCommit: null,
            TestedQemuVersion: null,
            KnownIssues: [],
            LastVerifiedAt: null);
}
