namespace LinuxCloth.Catalog;

public interface ICatalogSnapshotStore
{
    Task<CatalogSnapshot?> LoadLastKnownGoodAsync(CancellationToken cancellationToken = default);

    Task PromoteAsync(
        CatalogSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
