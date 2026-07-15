using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Storage;

namespace LinuxCloth.Cli.Tests;

public sealed class CatalogBundleResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"linuxcloth-cli-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ExplicitRootTakesPrecedenceOverEnvironment()
    {
        var explicitRoot = CreateCheckout("explicit");
        var environmentRoot = CreateCheckout("environment");
        var resolver = new CatalogBundleResolver(
            name => name == CatalogBundleResolver.CatalogRootEnvironmentVariable
                ? environmentRoot
                : null,
            _root,
            _root);

        var bundle = resolver.Resolve(explicitRoot);

        Assert.StartsWith(explicitRoot, bundle.CatalogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentRootIsUsedWhenOptionIsMissing()
    {
        var environmentRoot = CreateCheckout("environment");
        var resolver = new CatalogBundleResolver(
            name => name == CatalogBundleResolver.CatalogRootEnvironmentVariable
                ? environmentRoot
                : null,
            _root,
            _root);

        var bundle = resolver.Resolve(null);

        Assert.StartsWith(environmentRoot, bundle.CatalogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredCatalogIsMarkedForPromotionBeforeUse()
    {
        var environmentRoot = CreateCheckout("environment-promotion");
        var resolver = new CatalogBundleResolver(
            name => name == CatalogBundleResolver.CatalogRootEnvironmentVariable
                ? environmentRoot
                : null,
            _root,
            _root);

        var resolution = resolver.ResolveWithPolicy(null);

        Assert.Equal(CatalogBundleUsePolicy.RequiredOverride, resolution.UsePolicy);
        Assert.StartsWith(environmentRoot, resolution.Bundle.CatalogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentCheckoutRequiresSolutionMarker()
    {
        var checkout = CreateCheckout(Path.Combine("repo", "vendor", "TableClothCatalog"));
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(checkout))!;
        File.WriteAllText(Path.Combine(repository, "linuxcloth.slnx"), "<Solution />");
        var workingDirectory = Path.Combine(repository, "src", "LinuxCloth.Cli");
        Directory.CreateDirectory(workingDirectory);
        var resolver = new CatalogBundleResolver(_ => null, workingDirectory, _root);

        var bundle = resolver.Resolve(null);

        Assert.StartsWith(checkout, bundle.CatalogPath, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledCatalogBundleIsResolvedBeforeADevelopmentCheckout()
    {
        var installed = CreateDocsBundle(Path.Combine("app", "catalog"));
        var checkout = CreateCheckout(Path.Combine("repo", "vendor", "TableClothCatalog"));
        var repository = Path.GetDirectoryName(Path.GetDirectoryName(checkout))!;
        File.WriteAllText(Path.Combine(repository, "linuxcloth.slnx"), "<Solution />");
        var resolver = new CatalogBundleResolver(
            _ => null,
            repository,
            Path.GetDirectoryName(installed));

        var bundle = resolver.Resolve(null);

        Assert.Equal(Path.Combine(installed, "Catalog.xml"), bundle.CatalogPath);
        Assert.Equal(
            CatalogBundleUsePolicy.BundledFallback,
            resolver.ResolveWithPolicy(null).UsePolicy);
    }

    [Fact]
    public void ExplicitDocsDirectoryIsAccepted()
    {
        var docs = CreateDocsBundle("standalone-catalog");
        var resolver = new CatalogBundleResolver(_ => null, _root, _root);

        var bundle = resolver.Resolve(docs);

        Assert.Equal(Path.Combine(docs, "Catalog.xml"), bundle.CatalogPath);
    }

    [Fact]
    public async Task ExplicitCatalogReplacesAnExistingLastKnownGoodSnapshotBeforeQuery()
    {
        var first = CreateValidCheckout("first-valid", "첫 번째 이름");
        var paths = new LinuxClothPaths(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "runtime"));
        using (var initialWorkspace = new CatalogWorkspace(
                   paths,
                   new OfficialCatalogBundle(
                       Path.Combine(first, "docs", "Catalog.xml"),
                       Path.Combine(first, "docs", "images"),
                       OfficialCatalogBundle.OfficialRepository,
                       "1111111111111111111111111111111111111111")))
        {
            var initial = await initialWorkspace.InitializeAsync();
            Assert.Equal("첫 번째 이름", Assert.Single(initial.Services).Service.DisplayName);
        }

        var repository = FindRepositoryRoot();
        var pinned = Path.Combine(repository, "vendor", "TableClothCatalog");
        var services = new DefaultCliCommandServices(
            paths,
            new CatalogBundleResolver(_ => null, _root, _root));

        var replaced = await services.QueryCatalogAsync(null, null, pinned, CancellationToken.None);

        Assert.Equal(
            "우리은행 개인뱅킹",
            Assert.Single(replaced, entry => entry.Service.Id.Value == "WooriBank").Service.DisplayName);
    }

    [Fact]
    public void MissingCheckoutFailsCleanly()
    {
        Directory.CreateDirectory(_root);
        var resolver = new CatalogBundleResolver(_ => null, _root, _root);

        var exception = Assert.Throws<CatalogBundleResolutionException>(() => resolver.Resolve(null));

        Assert.Contains("--catalog-root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolicLinkCheckoutIsRejectedOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var checkout = CreateCheckout("real");
        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, checkout);
        var resolver = new CatalogBundleResolver(_ => null, _root, _root);

        Assert.Throws<CatalogBundleResolutionException>(() => resolver.Resolve(link));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateCheckout(string relativePath)
    {
        var checkout = Path.Combine(_root, relativePath);
        var docs = Path.Combine(checkout, "docs");
        Directory.CreateDirectory(Path.Combine(docs, "images"));
        File.WriteAllText(Path.Combine(docs, "Catalog.xml"), "<Catalog />");
        return checkout;
    }

    private string CreateDocsBundle(string relativePath)
    {
        var docs = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.Combine(docs, "images"));
        File.WriteAllText(Path.Combine(docs, "Catalog.xml"), "<Catalog />");
        File.WriteAllText(Path.Combine(docs, "LICENSE"), "Apache License 2.0");
        return docs;
    }

    private string CreateValidCheckout(string relativePath, string displayName)
    {
        var checkout = Path.Combine(_root, relativePath);
        var docs = Path.Combine(checkout, "docs");
        Directory.CreateDirectory(Path.Combine(docs, "images"));
        File.WriteAllText(
            Path.Combine(docs, "Catalog.xml"),
            $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <TableClothCatalog Fallback="ko-KR">
              <InternetServices>
                <Service Id="WooriBank" DisplayName="{{displayName}}" Category="Banking" Url="https://www.wooribank.com/" />
              </InternetServices>
            </TableClothCatalog>
            """);
        return checkout;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "vendor",
                    "TableClothCatalog",
                    "docs",
                    "Catalog.xml")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("linuxcloth repository root was not found.");
    }
}
