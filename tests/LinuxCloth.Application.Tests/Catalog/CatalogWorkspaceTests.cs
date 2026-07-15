using System.Text;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Application.Tests.Catalog;

public sealed class CatalogWorkspaceTests
{
    [Fact]
    public async Task SeedsTheExactPinnedOfficialCatalogAndKeepsTheFirstDuplicate()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var repositoryRoot = CatalogWorkspaceFixture.FindRepositoryRoot();
        var bundle = OfficialCatalogBundle.FromPinnedCheckout(
            Path.Combine(repositoryRoot, "vendor", "TableClothCatalog"));
        var officialBytes = await File.ReadAllBytesAsync(bundle.CatalogPath);
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle, new FixedTimeProvider());

        var state = await workspace.InitializeAsync();

        Assert.True(officialBytes.AsSpan().SequenceEqual(state.Snapshot.CatalogXml.Span));
        Assert.Equal(OfficialCatalogBundle.PinnedCommit, state.Snapshot.Manifest.UpstreamCommit);
        var duplicate = Assert.Single(
            state.Diagnostics,
            diagnostic => diagnostic.Code == CatalogDiagnosticCode.DuplicateServiceId);
        Assert.Equal("PayInfo", duplicate.ServiceId?.Value);
        Assert.True(state.TryGetService(ServiceId.Parse("PayInfo"), out var payInfo));
        Assert.Equal(CatalogCategory.Government, payInfo.Service.Category);
        Assert.True(state.TryGetService(ServiceId.Parse("WooriBank"), out var woori));
        Assert.NotNull(woori.Image);
        Assert.StartsWith(
            Path.Combine(
                fixture.Paths.CatalogDirectory,
                "assets",
                $"{state.Snapshot.Manifest.CatalogSha256}-{state.Snapshot.Manifest.UpstreamCommit}"),
            woori.Image.Path,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingLastKnownGoodSnapshotWinsOverANewerBundledFallback()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var firstBundle = fixture.CreateBundle("first", "처음 이름");
        using (var firstWorkspace = new CatalogWorkspace(fixture.Paths, firstBundle))
        {
            _ = await firstWorkspace.InitializeAsync();
        }

        var fallback = fixture.CreateBundle(
            "fallback",
            "번들 새 이름",
            "2222222222222222222222222222222222222222");
        using var workspace = new CatalogWorkspace(fixture.Paths, fallback);

        var state = await workspace.InitializeAsync();

        Assert.Equal("처음 이름", Assert.Single(state.Search("처음")).Service.DisplayName);
        Assert.Empty(state.Search("번들 새"));
        Assert.Equal("1111111111111111111111111111111111111111", state.Snapshot.Manifest.UpstreamCommit);
    }

    [Fact]
    public async Task PromotesCatalogAndAssetsTogetherAndRollsBackFromCorruptCurrent()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var firstBundle = fixture.CreateBundle("first", "첫 번째");
        var secondBundle = fixture.CreateBundle(
            "second",
            "두 번째",
            "2222222222222222222222222222222222222222");
        using (var workspace = new CatalogWorkspace(fixture.Paths, firstBundle))
        {
            var first = await workspace.InitializeAsync();
            var second = await workspace.PromoteBundleAsync(secondBundle);
            Assert.NotEqual(
                first.Snapshot.Manifest.CatalogSha256,
                second.Snapshot.Manifest.CatalogSha256);
            Assert.Equal("두 번째", Assert.Single(second.Search("두 번째")).Service.DisplayName);
            Assert.NotNull(Assert.Single(second.Search("두 번째")).Image);
        }

        var currentHash = (await File.ReadAllTextAsync(
                Path.Combine(fixture.Paths.CatalogDirectory, "current")))
            .Trim();
        await File.WriteAllTextAsync(
            Path.Combine(
                fixture.Paths.CatalogDirectory,
                "snapshots",
                currentHash,
                "Catalog.xml"),
            "<corrupt />");

        using var recoveredWorkspace = new CatalogWorkspace(fixture.Paths, firstBundle);
        var recovered = await recoveredWorkspace.InitializeAsync();

        Assert.Equal("첫 번째", Assert.Single(recovered.Search("첫 번째")).Service.DisplayName);
        Assert.NotNull(Assert.Single(recovered.Search("첫 번째")).Image);
    }

    [Fact]
    public async Task InvalidUpdateLeavesPublishedAndPersistedCatalogUnchanged()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("valid");
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);
        var initial = await workspace.InitializeAsync();
        var invalid = fixture.CreateBundle(
            "invalid",
            "invalid",
            "2222222222222222222222222222222222222222");
        await File.WriteAllTextAsync(invalid.CatalogPath, "<not-a-catalog />");

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => workspace.PromoteBundleAsync(invalid));

        Assert.Same(initial, workspace.Current);
        using var reopened = new CatalogWorkspace(fixture.Paths, bundle);
        var persisted = await reopened.InitializeAsync();
        Assert.Equal(initial.Snapshot.Manifest.CatalogSha256, persisted.Snapshot.Manifest.CatalogSha256);
    }

    [Fact]
    public async Task RejectsDifferentProvenanceForIdenticalCatalogBytesInsteadOfMixingAssets()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var first = fixture.CreateBundle("first");
        var second = fixture.CreateBundle(
            "second",
            commit: "2222222222222222222222222222222222222222");
        await File.AppendAllTextAsync(
            Path.Combine(second.ImagesDirectory, "Banking", "WooriBank.png"),
            "new-image-bytes");
        using var workspace = new CatalogWorkspace(fixture.Paths, first);
        var initial = await workspace.InitializeAsync();

        var exception = await Assert.ThrowsAsync<CatalogWorkspaceException>(
            () => workspace.PromoteBundleAsync(second));

        Assert.Contains("different upstream provenance", exception.Message, StringComparison.Ordinal);
        Assert.Same(initial, workspace.Current);
        Assert.Contains(
            first.UpstreamCommit,
            Assert.Single(initial.Search("Woori")).Image!.Path,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExposesSearchCategoriesDetailsAndComposedCompatibility()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("catalog");
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);
        _ = await workspace.InitializeAsync();
        var overlay = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 1,
              "services": [
                {
                  "serviceId": "WooriBank",
                  "status": "verified",
                  "preferredDisplay": "spice",
                  "knownIssues": ["콘솔 세션 사용"],
                  "lastVerifiedAt": "2026-07-15"
                }
              ]
            }
            """);

        var state = await workspace.ApplyCompatibilityOverlayAsync(overlay);

        Assert.Equal([CatalogCategory.Banking, CatalogCategory.Government], state.Categories);
        Assert.Equal("WooriBank", Assert.Single(workspace.Search("Woori")).Service.Id.Value);
        Assert.Single(workspace.GetByCategory(CatalogCategory.Government));
        Assert.True(workspace.TryGetService(ServiceId.Parse("WooriBank"), out var woori));
        Assert.Equal(CompatibilityStatus.Verified, woori.Compatibility.Status);
        Assert.Equal(new DateOnly(2026, 7, 15), woori.Compatibility.LastVerifiedAt);
        Assert.True(workspace.TryGetService(ServiceId.Parse("Gov24"), out var gov24));
        Assert.Equal(CompatibilityStatus.Untested, gov24.Compatibility.Status);

        using var reopened = new CatalogWorkspace(fixture.Paths, bundle);
        var persisted = await reopened.InitializeAsync();
        Assert.Equal(
            CompatibilityStatus.Verified,
            Assert.Single(persisted.Search("Woori")).Compatibility.Status);
    }

    [Fact]
    public async Task ResolvesOnlyCatalogBackedSelectionsForDisposableLaunch()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("catalog");
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);
        var state = await workspace.InitializeAsync();

        var resolution = await workspace.ResolveAsync(
            [ServiceId.Parse("Gov24"), ServiceId.Parse("WooriBank")]);

        Assert.Equal(["Gov24", "WooriBank"], resolution.ServiceIds.Select(id => id.Value));
        Assert.Equal(["정부24", "우리은행"], resolution.DisplayNames);
        Assert.Equal(
            state.Snapshot.Manifest.CatalogSha256.ToLowerInvariant(),
            resolution.CatalogSha256);
        Assert.Equal(
            Path.Combine(
                fixture.Paths.CatalogDirectory,
                "snapshots",
                state.Snapshot.Manifest.CatalogSha256,
                "Catalog.xml"),
            resolution.CatalogPath);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            workspace.ResolveAsync([ServiceId.Parse("MissingService")]));
    }

    [Fact]
    public async Task InvalidOverlayDoesNotReplaceTheCurrentOverlay()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("catalog");
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);
        _ = await workspace.InitializeAsync();
        var valid = Encoding.UTF8.GetBytes(
            """{"schemaVersion":1,"services":[{"serviceId":"WooriBank","status":"partial"}]}""");
        _ = await workspace.ApplyCompatibilityOverlayAsync(valid);
        var overlayPath = Path.Combine(fixture.Paths.CompatibilityDirectory, "current.json");
        var persisted = await File.ReadAllBytesAsync(overlayPath);

        await Assert.ThrowsAsync<CatalogValidationException>(() =>
            workspace.ApplyCompatibilityOverlayAsync(
                """{"schemaVersion":1,"services":"invalid"}"""u8.ToArray()));

        Assert.Equal(
            CompatibilityStatus.Partial,
            Assert.Single(workspace.Search("Woori")).Compatibility.Status);
        var persistedAfterFailure = await File.ReadAllBytesAsync(overlayPath);
        Assert.Equal(persisted, persistedAfterFailure);
    }

    [Fact]
    public async Task OversizedCatalogIsRejectedBeforeItCanReplaceCurrent()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("valid");
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);
        var initial = await workspace.InitializeAsync();
        var oversized = fixture.CreateBundle(
            "oversized",
            commit: "2222222222222222222222222222222222222222");
        await using (var stream = new FileStream(
                         oversized.CatalogPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(CatalogParser.MaximumDocumentBytes + 1L);
        }

        await Assert.ThrowsAsync<CatalogWorkspaceException>(
            () => workspace.PromoteBundleAsync(oversized));

        Assert.Same(initial, workspace.Current);
    }

    [Fact]
    public async Task SymbolicLinkImageIsNeverImportedOrResolved()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("symlink");
        var imagePath = Path.Combine(bundle.ImagesDirectory, "Banking", "WooriBank.png");
        var outside = Path.Combine(fixture.Root, "outside.png");
        File.Move(imagePath, outside);
        File.CreateSymbolicLink(imagePath, outside);
        using var workspace = new CatalogWorkspace(fixture.Paths, bundle);

        var exception = await Assert.ThrowsAsync<CatalogWorkspaceException>(
            () => workspace.InitializeAsync());

        Assert.Contains("regular file", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.CatalogDirectory, "current")));
    }

    [Fact]
    public async Task TamperedImageSnapshotFailsClosedOnReload()
    {
        using var fixture = new CatalogWorkspaceFixture();
        var bundle = fixture.CreateBundle("catalog");
        string imagePath;
        using (var workspace = new CatalogWorkspace(fixture.Paths, bundle))
        {
            var state = await workspace.InitializeAsync();
            imagePath = Assert.Single(state.Search("Woori")).Image!.Path;
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                imagePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await File.AppendAllTextAsync(imagePath, "tampered");
        using var reopened = new CatalogWorkspace(fixture.Paths, bundle);

        var exception = await Assert.ThrowsAsync<CatalogWorkspaceException>(
            () => reopened.InitializeAsync());

        Assert.Contains("manifest length", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
    }
}
