using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class DistributionInfoReaderTests
{
    [Fact]
    public void ParsesQuotedValuesAndDetectsDebianDerivatives()
    {
        const string contents = """
            NAME="Example \"Linux\""
            ID=example
            ID_LIKE="ubuntu debian"
            VERSION_ID='24.04'
            """;

        var distribution = DistributionInfoReader.Parse(contents);

        Assert.Equal("example", distribution.Id);
        Assert.Equal("Example \"Linux\"", distribution.Name);
        Assert.Equal("24.04", distribution.VersionId);
        Assert.Equal(["ubuntu", "debian"], distribution.IdLike);
        Assert.Equal(DistributionFamily.Debian, distribution.Family);
    }

    [Fact]
    public void UsesIdLikeToDetectFedoraFamily()
    {
        var distribution = DistributionInfoReader.Parse("ID=custom\nID_LIKE=\"rhel fedora\"\n");

        Assert.Equal(DistributionFamily.Fedora, distribution.Family);
    }

    [Fact]
    public void DetectsArchFamilyOnlyForPackageRemediationHints()
    {
        var distribution = DistributionInfoReader.Parse("ID=custom\nID_LIKE=arch\n");

        Assert.Equal(DistributionFamily.Arch, distribution.Family);
    }

    [Fact]
    public void RejectsDuplicateKeysInsteadOfSilentlyOverridingThem()
    {
        Assert.Throws<InvalidDataException>(() =>
            DistributionInfoReader.Parse("ID=debian\nID=fedora\n"));
    }

    [Fact]
    public void RejectsShellSyntaxInUnquotedValues()
    {
        Assert.Throws<InvalidDataException>(() =>
            DistributionInfoReader.Parse("ID=debian;touch /tmp/unsafe\n"));
    }
}
