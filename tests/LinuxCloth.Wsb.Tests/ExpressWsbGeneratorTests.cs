using LinuxCloth.Core;

namespace LinuxCloth.Wsb.Tests;

public sealed class ExpressWsbGeneratorTests
{
    [Fact]
    public void GeneratesTheSimplifiedOfficialExpressContractWithSecureDefaults()
    {
        var serviceIds = new[] { ServiceId.Parse("WooriBank"), ServiceId.Parse("KB") };

        var xml = ExpressWsbGenerator.Generate(serviceIds);
        var parsed = WsbParser.Parse(xml);

        Assert.Equal(WsbFeatureState.Enable, parsed.Networking);
        Assert.Equal(WsbFeatureState.Disable, parsed.VirtualGpu);
        Assert.Equal(WsbFeatureState.Disable, parsed.ClipboardRedirection);
        Assert.Null(parsed.MemoryInMiB);
        Assert.Empty(parsed.MappedFolders);
        Assert.True(parsed.IsValidatedExpress);
        Assert.Equal(serviceIds, parsed.ExpressServiceIds);
        Assert.Contains("$env:TABLECLOTH_SITE_IDS = ''WooriBank KB''", parsed.LogonCommand, StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/yourtablecloth/TableCloth/releases/latest/download/tablecloth-prepare.ps1",
            parsed.LogonCommand,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MappedFolders", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var serviceIds = new[] { ServiceId.Parse("WooriBank"), ServiceId.Parse("KB") };

        Assert.Equal(
            ExpressWsbGenerator.Generate(serviceIds),
            ExpressWsbGenerator.Generate(serviceIds));
    }

    [Fact]
    public void SessionPolicyIsRepresentedExplicitly()
    {
        var xml = ExpressWsbGenerator.Generate(
            [ServiceId.Parse("WooriBank")],
            networkEnabled: false,
            clipboardEnabled: true,
            memoryInMiB: 8192);

        var parsed = WsbParser.Parse(xml);

        Assert.Equal(WsbFeatureState.Disable, parsed.Networking);
        Assert.Equal(WsbFeatureState.Enable, parsed.ClipboardRedirection);
        Assert.Equal(8192, parsed.MemoryInMiB);
    }

    [Fact]
    public void DefaultOrDuplicateServiceIdentifiersAreRejected()
    {
        var id = ServiceId.Parse("WooriBank");

        Assert.Throws<ArgumentException>(() => ExpressWsbGenerator.Generate([default]));
        Assert.Throws<ArgumentException>(() => ExpressWsbGenerator.Generate([id, id]));
    }

    [Fact]
    public void InjectedSiteTextCannotPassNormalModeValidation()
    {
        var xml = ExpressWsbGenerator.Generate([ServiceId.Parse("WooriBank")]);
        var injected = xml.Replace(
            "WooriBank",
            "WooriBank''; Stop-Process -Name explorer; #",
            StringComparison.Ordinal);

        Assert.Throws<WsbValidationException>(() => WsbParser.Parse(injected));
    }
}
