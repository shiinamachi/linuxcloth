namespace LinuxCloth.Catalog;

public sealed class CatalogSnapshot
{
    private readonly byte[] _catalogXml;

    private CatalogSnapshot(
        byte[] catalogXml,
        CatalogDocument catalog,
        CatalogSnapshotManifest manifest)
    {
        _catalogXml = catalogXml;
        Catalog = catalog;
        Manifest = manifest;
    }

    public ReadOnlyMemory<byte> CatalogXml => _catalogXml;

    public CatalogDocument Catalog { get; }

    public CatalogSnapshotManifest Manifest { get; }

    public static CatalogSnapshot Create(
        ReadOnlyMemory<byte> catalogXml,
        CatalogParser parser,
        string upstreamRepository,
        string upstreamCommit,
        DateTimeOffset retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(parser);

        var bytes = catalogXml.ToArray();
        var catalog = parser.Parse(bytes);
        var manifest = new CatalogSnapshotManifest(
            CatalogSnapshotManifest.CurrentSchemaVersion,
            upstreamRepository,
            upstreamCommit,
            CatalogSnapshotManifest.ComputeCatalogSha256(bytes),
            retrievedAt);
        return new CatalogSnapshot(bytes, catalog, manifest);
    }

    internal static CatalogSnapshot FromPersisted(
        byte[] catalogXml,
        CatalogSnapshotManifest manifest,
        CatalogParser parser)
    {
        var actualHash = CatalogSnapshotManifest.ComputeCatalogSha256(catalogXml);
        if (!string.Equals(actualHash, manifest.CatalogSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogValidationException(
                "The persisted catalog does not match its snapshot manifest SHA-256 digest.");
        }

        return new CatalogSnapshot(catalogXml, parser.Parse(catalogXml), manifest);
    }

    internal void Verify()
    {
        var actualHash = CatalogSnapshotManifest.ComputeCatalogSha256(_catalogXml);
        if (!string.Equals(actualHash, Manifest.CatalogSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CatalogValidationException(
                "The catalog snapshot does not match its manifest SHA-256 digest.");
        }
    }
}
