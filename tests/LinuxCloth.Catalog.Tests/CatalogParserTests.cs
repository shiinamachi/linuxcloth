using System.Text;

namespace LinuxCloth.Catalog.Tests;

public sealed class CatalogParserTests
{
    private readonly CatalogParser _parser = new();

    [Fact]
    public void ParsesRepresentativeOfficialServiceFields()
    {
        var catalog = _parser.Parse(Fixture.ReadBytes("Catalog.xml"));

        Assert.Equal("ko-KR", catalog.FallbackLanguage);
        Assert.Equal(3, catalog.Services.Count);

        var service = Assert.Single(catalog.Services, item => item.Id.Value == "WooriBank");
        Assert.Equal("우리은행 개인뱅킹", service.DisplayName);
        Assert.Equal("WOORI BANK", service.EnglishDisplayName);
        Assert.Equal(CatalogCategory.Banking, service.Category);
        Assert.Equal("https://www.wooribank.com/", service.Url.AbsoluteUri);
        Assert.Equal("SPICE 콘솔을 사용하세요.", service.CompatNotes);
        Assert.Equal("Use the SPICE console.", service.EnglishCompatNotes);
        Assert.Equal(["우리", "우리은행", "woori"], service.SearchKeywords);
        Assert.Equal(2, service.Packages.Count);
        Assert.Equal("/silent", service.Packages[0].Arguments);
        Assert.Equal(Uri.UriSchemeHttp, service.Packages[1].Url.Scheme);
        var extension = Assert.Single(service.EdgeExtensions);
        Assert.Equal("abcdefghijklmnop", extension.ExtensionId);
        Assert.True(service.HasCustomBootstrap);
        Assert.Contains("Write-Output", service.CustomBootstrap, StringComparison.Ordinal);

        Assert.False(catalog.Services.Single(item => item.Id.Value == "Gov24").HasCustomBootstrap);
    }

    [Fact]
    public void RejectsExternalEntityDefinitions()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE TableClothCatalog [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <TableClothCatalog>
              <InternetServices>
                <Service Id="Unsafe" DisplayName="&secret;" Category="Other" Url="https://example.com" />
              </InternetServices>
            </TableClothCatalog>
            """;

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void RejectsUnexpectedDefaultNamespace()
    {
        const string xml = "<TableClothCatalog xmlns=\"urn:unexpected\" />";

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void ParsesThePinnedOfficialCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "OfficialCatalog.xml");
        if (!File.Exists(path))
        {
            return;
        }

        var catalog = new CatalogParser(CatalogDuplicateIdPolicy.KeepFirst)
            .Parse(File.ReadAllBytes(path));

        Assert.True(catalog.Services.Count >= 250);
        Assert.Contains(catalog.Services, static service => service.Id.Value == "WooriBank");
        var diagnostic = Assert.Single(catalog.Diagnostics);
        Assert.Equal(CatalogDiagnosticCode.DuplicateServiceId, diagnostic.Code);
        Assert.Equal("PayInfo", diagnostic.ServiceId?.Value);
    }

    [Fact]
    public void RejectsDuplicateServiceIdentifiers()
    {
        const string services = """
            <Service Id="Duplicate" DisplayName="One" Category="Other" Url="https://example.com/one" />
            <Service Id="Duplicate" DisplayName="Two" Category="Other" Url="https://example.com/two" />
            """;

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(CatalogWith(services)));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("은행")]
    [InlineData("../escape")]
    public void RejectsInvalidServiceIdentifiers(string serviceId)
    {
        var service = $"<Service Id=\"{serviceId}\" DisplayName=\"Bad\" Category=\"Other\" Url=\"https://example.com\" />";

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(CatalogWith(service)));
    }

    [Theory]
    [InlineData("ftp://example.com/catalog")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative")]
    [InlineData("https:///missing-host")]
    public void RejectsNonWebServiceUrls(string url)
    {
        var service = $"<Service Id=\"BadUrl\" DisplayName=\"Bad\" Category=\"Other\" Url=\"{url}\" />";

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(CatalogWith(service)));
    }

    [Fact]
    public void RejectsInvalidPackageAndExtensionUrls()
    {
        const string package = """
            <Service Id="BadPackage" DisplayName="Bad" Category="Other" Url="https://example.com">
              <Packages><Package Name="Unsafe" Url="file:///tmp/payload.exe" /></Packages>
            </Service>
            """;
        const string extension = """
            <Service Id="BadExtension" DisplayName="Bad" Category="Other" Url="https://example.com">
              <EdgeExtensions><EdgeExtension Name="Unsafe" CrxUrl="ftp://example.com/payload.crx" /></EdgeExtensions>
            </Service>
            """;

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(CatalogWith(package)));
        Assert.Throws<CatalogValidationException>(() => _parser.Parse(CatalogWith(extension)));
    }

    [Fact]
    public void RejectsDocumentsLargerThanSixteenMebibytes()
    {
        using var stream = new MemoryStream(new byte[CatalogParser.MaximumDocumentBytes + 1]);

        Assert.Throws<CatalogValidationException>(() => _parser.Parse(stream));
    }

    private static byte[] CatalogWith(string services) => Encoding.UTF8.GetBytes(
        $"<TableClothCatalog><InternetServices>{services}</InternetServices></TableClothCatalog>");
}
