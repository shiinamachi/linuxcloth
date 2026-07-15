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
}
