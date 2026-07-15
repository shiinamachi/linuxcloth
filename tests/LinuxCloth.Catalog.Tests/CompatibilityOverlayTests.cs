using System.Text;
using LinuxCloth.Core;

namespace LinuxCloth.Catalog.Tests;

public sealed class CompatibilityOverlayTests
{
    private readonly CompatibilityOverlayParser _parser = new();

    [Fact]
    public void ParsesAndComposesLinuxCompatibilityWithoutChangingCatalogServices()
    {
        var overlay = _parser.Parse(Fixture.ReadBytes("CompatibilityOverlay.json"));
        var catalog = new CatalogParser().Parse(Fixture.ReadBytes("Catalog.xml"));

        Assert.True(overlay.TryGet(ServiceId.Parse("WooriBank"), out var woori));
        Assert.NotNull(woori);
        Assert.Equal(CompatibilityStatus.Verified, woori.Status);
        Assert.Equal(new DateOnly(2026, 7, 14), woori.LastVerifiedAt);

        var composed = CatalogComposer.Compose(catalog, overlay);
        Assert.Equal(3, composed.Count);
        Assert.Equal(
            CompatibilityStatus.Untested,
            composed.Single(item => item.Service.Id.Value == "KookminBank").Compatibility.Status);
        Assert.Equal(
            DisplayMode.QemuConsole,
            composed.Single(item => item.Service.Id.Value == "Gov24").Compatibility.PreferredDisplay);
    }

    [Fact]
    public void RejectsDuplicateAndInvalidOverlayRecords()
    {
        const string duplicate = """
            {"schemaVersion":1,"services":[
              {"serviceId":"Same","status":"verified"},
              {"serviceId":"Same","status":"partial"}
            ]}
            """;
        const string invalid = """
            {"schemaVersion":1,"services":[
              {"serviceId":"bad id","status":"unknown"}
            ]}
            """;

        Assert.Throws<CatalogValidationException>(
            () => _parser.Parse(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<CatalogValidationException>(
            () => _parser.Parse(Encoding.UTF8.GetBytes(invalid)));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"services\":[],\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"services\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"services\":[{\"serviceId\":\"Bank\",\"status\":\"verified\",\"unknown\":true}]}")]
    [InlineData("{\"schemaVersion\":1,\"services\":[{\"serviceId\":\"Bank\",\"serviceId\":\"Bank\",\"status\":\"verified\"}]}")]
    public void RejectsUnknownAndDuplicateProperties(string json)
    {
        Assert.Throws<CatalogValidationException>(
            () => _parser.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void RejectsTooManyServices()
    {
        var services = Enumerable.Range(0, CompatibilityOverlayParser.MaximumServiceCount + 1)
            .Select(index => $"{{\"serviceId\":\"Service{index}\",\"status\":\"untested\"}}");
        var document = $"{{\"schemaVersion\":1,\"services\":[{string.Join(',', services)}]}}";

        Assert.Throws<CatalogValidationException>(
            () => _parser.Parse(Encoding.UTF8.GetBytes(document)));
    }

    [Fact]
    public void RejectsOversizedKnownIssues()
    {
        var issue = new string('x', CompatibilityOverlayParser.MaximumKnownIssueLength + 1);
        var document = $$"""
            {"schemaVersion":1,"services":[{
              "serviceId":"Bank",
              "status":"partial",
              "knownIssues":["{{issue}}"]
            }]}
            """;

        Assert.Throws<CatalogValidationException>(
            () => _parser.Parse(Encoding.UTF8.GetBytes(document)));
    }
}
