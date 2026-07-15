using System.Text;
using LinuxCloth.Core;
using LinuxCloth.Wsb;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class GuestConfigResolverTests
{
    [Fact]
    public void RequiresExactlyOneValidConfig()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        ConfigFixture.WriteValid(first.Path);
        ConfigFixture.WriteValid(second.Path);
        var resolver = CreateResolver(first.Path, second.Path);

        var resolution = resolver.Resolve();

        Assert.Equal(ConfigResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Manifest);
    }

    [Fact]
    public void RejectsManifestHashMismatch()
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        File.WriteAllText(
            Path.Combine(directory.Path, GuestConfigStager.LaunchManifestHashFileName),
            $"{new string('0', 64)}  {GuestConfigStager.LaunchManifestFileName}\n",
            Encoding.ASCII);

        var resolution = CreateResolver(directory.Path).Resolve();

        Assert.Equal(ConfigResolutionStatus.Invalid, resolution.Status);
    }

    [Fact]
    public void RejectsNonCanonicalUppercaseHashSidecar()
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(directory.Path);
        var sidecarPath = Path.Combine(
            directory.Path,
            GuestConfigStager.LaunchManifestHashFileName);
        var sidecar = File.ReadAllText(sidecarPath, Encoding.ASCII);
        File.WriteAllText(
            sidecarPath,
            sidecar[..64].ToUpperInvariant() + sidecar[64..],
            Encoding.ASCII);

        var resolution = CreateResolver(directory.Path).Resolve();

        Assert.Equal(ConfigResolutionStatus.Invalid, resolution.Status);
    }

    [Fact]
    public void RejectsCatalogHashMismatch()
    {
        using var directory = new TemporaryDirectory();
        ConfigFixture.WriteValid(
            directory.Path,
            catalogBytes: Encoding.UTF8.GetBytes("<expected />"),
            includeCatalog: true);
        File.WriteAllText(
            Path.Combine(directory.Path, GuestConfigStager.CatalogFileName),
            "<different />",
            Encoding.UTF8);

        var resolution = CreateResolver(directory.Path).Resolve();

        Assert.Equal(ConfigResolutionStatus.Invalid, resolution.Status);
    }

    [Fact]
    public void AcceptsOneValidConfigAndAnInvalidCandidate()
    {
        using var valid = new TemporaryDirectory();
        using var invalid = new TemporaryDirectory();
        var expected = ConfigFixture.WriteValid(
            valid.Path,
            [ServiceId.Parse("WooriBank")]);
        ConfigFixture.WriteValid(invalid.Path);
        File.WriteAllText(
            Path.Combine(invalid.Path, GuestConfigStager.LaunchManifestHashFileName),
            "invalid\n",
            Encoding.ASCII);

        var resolution = CreateResolver(valid.Path, invalid.Path).Resolve();

        Assert.Equal(ConfigResolutionStatus.Success, resolution.Status);
        Assert.NotNull(resolution.Manifest);
        Assert.Equal(expected.SessionId, resolution.Manifest.SessionId);
        Assert.Equal(expected.ServiceIds, resolution.Manifest.ServiceIds);
        Assert.Equal(expected.CatalogSha256, resolution.Manifest.CatalogSha256);
    }

    private static GuestConfigResolver CreateResolver(params string[] roots) =>
        new(new FakeDriveProvider(roots), NullDiagnosticLog.Instance);
}
