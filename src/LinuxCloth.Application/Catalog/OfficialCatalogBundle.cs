namespace LinuxCloth.Application.Catalog;

public sealed record OfficialCatalogBundle
{
    public const string OfficialRepository = "yourtablecloth/TableClothCatalog";
    public const string PinnedCommit = "7e3e6a8f54d5e273dad61667024e372cc2958dd9";

    public OfficialCatalogBundle(
        string catalogPath,
        string imagesDirectory,
        string upstreamRepository,
        string upstreamCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamCommit);

        if (upstreamRepository.Length > 512 || upstreamRepository.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The upstream repository provenance is invalid.",
                nameof(upstreamRepository));
        }

        if (upstreamCommit.Length is not (40 or 64) || !upstreamCommit.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The upstream commit must be a complete 40- or 64-character hexadecimal object identifier.",
                nameof(upstreamCommit));
        }

        CatalogPath = Path.GetFullPath(catalogPath);
        ImagesDirectory = Path.GetFullPath(imagesDirectory);
        UpstreamRepository = upstreamRepository;
        UpstreamCommit = upstreamCommit.ToLowerInvariant();
    }

    public string CatalogPath { get; }

    public string ImagesDirectory { get; }

    public string UpstreamRepository { get; }

    public string UpstreamCommit { get; }

    public static OfficialCatalogBundle FromPinnedCheckout(string checkoutDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutDirectory);

        var root = Path.GetFullPath(checkoutDirectory);
        var documentation = Path.Combine(root, "docs");
        return new OfficialCatalogBundle(
            Path.Combine(documentation, "Catalog.xml"),
            Path.Combine(documentation, "images"),
            OfficialRepository,
            PinnedCommit);
    }
}
