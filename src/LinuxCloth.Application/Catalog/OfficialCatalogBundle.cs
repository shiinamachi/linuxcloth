namespace LinuxCloth.Application.Catalog;

public sealed record OfficialCatalogBundle
{
    public const string OfficialRepository = "yourtablecloth/TableClothCatalog";
    public const string PinnedCommit = "7e3e6a8f54d5e273dad61667024e372cc2958dd9";
    public const string PinnedCatalogSha256 =
        "6198D7F3ABB6744991D0A1A2400E75F1E8A588470EF9AB765B8B11354C3F968A";
    public const string PinnedImagesSha256 =
        "C21D6D6E6C1CFE791DF913F497D1F0112D5BB669CD5355AF6D491ED4AC5CFC4A";

    public OfficialCatalogBundle(
        string catalogPath,
        string imagesDirectory,
        string upstreamRepository,
        string upstreamCommit,
        string? expectedCatalogSha256 = null,
        string? expectedImagesSha256 = null)
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

        if (expectedCatalogSha256 is not null &&
            (expectedCatalogSha256.Length != 64 || !expectedCatalogSha256.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException(
                "The expected catalog digest must be a complete SHA-256 value.",
                nameof(expectedCatalogSha256));
        }

        if (expectedImagesSha256 is not null &&
            (expectedImagesSha256.Length != 64 || !expectedImagesSha256.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException(
                "The expected image-tree digest must be a complete SHA-256 value.",
                nameof(expectedImagesSha256));
        }

        CatalogPath = Path.GetFullPath(catalogPath);
        ImagesDirectory = Path.GetFullPath(imagesDirectory);
        UpstreamRepository = upstreamRepository;
        UpstreamCommit = upstreamCommit.ToLowerInvariant();
        ExpectedCatalogSha256 = expectedCatalogSha256?.ToUpperInvariant();
        ExpectedImagesSha256 = expectedImagesSha256?.ToUpperInvariant();
    }

    public string CatalogPath { get; }

    public string ImagesDirectory { get; }

    public string UpstreamRepository { get; }

    public string UpstreamCommit { get; }

    public string? ExpectedCatalogSha256 { get; }

    public string? ExpectedImagesSha256 { get; }

    public static OfficialCatalogBundle FromPinnedCheckout(string checkoutDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutDirectory);

        var root = Path.GetFullPath(checkoutDirectory);
        var documentation = Path.Combine(root, "docs");
        return new OfficialCatalogBundle(
            Path.Combine(documentation, "Catalog.xml"),
            Path.Combine(documentation, "images"),
            OfficialRepository,
            PinnedCommit,
            PinnedCatalogSha256,
            PinnedImagesSha256);
    }

    public static OfficialCatalogBundle FromPinnedDocsDirectory(string docsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docsDirectory);

        var documentation = Path.GetFullPath(docsDirectory);
        return new OfficialCatalogBundle(
            Path.Combine(documentation, "Catalog.xml"),
            Path.Combine(documentation, "images"),
            OfficialRepository,
            PinnedCommit,
            PinnedCatalogSha256,
            PinnedImagesSha256);
    }
}
