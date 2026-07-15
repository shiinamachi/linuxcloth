using System.Net;
using System.Text;

namespace LinuxCloth.Catalog.Tests;

public sealed class CatalogSnapshotTests
{
    private static readonly DateTimeOffset RetrievedAt =
        new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManifestRoundTripPreservesSha256AndProvenance()
    {
        var bytes = Fixture.ReadBytes("Catalog.xml");
        var snapshot = CatalogSnapshot.Create(
            bytes,
            new CatalogParser(),
            "yourtablecloth/TableClothCatalog",
            "abc123",
            RetrievedAt);

        var manifest = CatalogSnapshotManifest.Parse(snapshot.Manifest.ToJson());

        Assert.Equal(CatalogSnapshotManifest.ComputeCatalogSha256(bytes), manifest.CatalogSha256);
        Assert.Equal("yourtablecloth/TableClothCatalog", manifest.UpstreamRepository);
        Assert.Equal("abc123", manifest.UpstreamCommit);
        Assert.Equal(RetrievedAt, manifest.RetrievedAt);
    }

    [Fact]
    public async Task InvalidHttpUpdateKeepsTheLastKnownGoodSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var parser = new CatalogParser();
        using var store = new FileCatalogSnapshotStore(directory.Path, parser);
        using var handler = new QueueHttpMessageHandler(
            XmlResponse(Fixture.ReadBytes("Catalog.xml")),
            XmlResponse(Encoding.UTF8.GetBytes("<not-a-catalog />")));
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var updater = new CatalogSnapshotUpdater(
            httpClient,
            parser,
            store,
            new FixedTimeProvider(RetrievedAt));
        var uri = new Uri("https://catalog.example.test/Catalog.xml");

        var first = await updater.UpdateAsync(
            uri,
            "yourtablecloth/TableClothCatalog",
            "first");
        await Assert.ThrowsAsync<CatalogValidationException>(
            () => updater.UpdateAsync(
                uri,
                "yourtablecloth/TableClothCatalog",
                "invalid"));

        var loaded = await store.LoadLastKnownGoodAsync();
        Assert.NotNull(loaded);
        Assert.Equal(first.Manifest.CatalogSha256, loaded.Manifest.CatalogSha256);
        Assert.Equal("first", loaded.Manifest.UpstreamCommit);
    }

    [Fact]
    public async Task CorruptCurrentSnapshotRollsBackToPreviousSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var parser = new CatalogParser();
        using var store = new FileCatalogSnapshotStore(directory.Path, parser);
        var first = CatalogSnapshot.Create(
            Fixture.ReadBytes("Catalog.xml"),
            parser,
            "yourtablecloth/TableClothCatalog",
            "first",
            RetrievedAt);
        var secondBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(Fixture.ReadBytes("Catalog.xml"))
                .Replace("정부24", "정부24 최신", StringComparison.Ordinal));
        var second = CatalogSnapshot.Create(
            secondBytes,
            parser,
            "yourtablecloth/TableClothCatalog",
            "second",
            RetrievedAt.AddHours(1));

        await store.PromoteAsync(first);
        await store.PromoteAsync(second);

        var currentHash = (await File.ReadAllTextAsync(Path.Combine(directory.Path, "current"))).Trim();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "snapshots", currentHash, "Catalog.xml"),
            "<corrupt />");

        var loaded = await store.LoadLastKnownGoodAsync();
        Assert.NotNull(loaded);
        Assert.Equal("first", loaded.Manifest.UpstreamCommit);
    }

    [Fact]
    public async Task PromotionDoesNotReplacePreviousPointerWithCorruptCurrentSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var parser = new CatalogParser();
        using var store = new FileCatalogSnapshotStore(directory.Path, parser);
        var first = CreateSnapshot(parser, "first", "정부24 첫 번째", 0);
        var second = CreateSnapshot(parser, "second", "정부24 두 번째", 1);
        var third = CreateSnapshot(parser, "third", "정부24 세 번째", 2);

        await store.PromoteAsync(first);
        await store.PromoteAsync(second);
        var corruptHash = (await File.ReadAllTextAsync(Path.Combine(directory.Path, "current"))).Trim();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "snapshots", corruptHash, "Catalog.xml"),
            "<corrupt />");

        await store.PromoteAsync(third);
        var thirdHash = (await File.ReadAllTextAsync(Path.Combine(directory.Path, "current"))).Trim();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "snapshots", thirdHash, "Catalog.xml"),
            "<corrupt />");

        var loaded = await store.LoadLastKnownGoodAsync();
        Assert.NotNull(loaded);
        Assert.Equal("first", loaded.Manifest.UpstreamCommit);
    }

    private static HttpResponseMessage XmlResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new("application/xml") },
            },
        };

    private static CatalogSnapshot CreateSnapshot(
        CatalogParser parser,
        string commit,
        string displayName,
        int hours) =>
        CatalogSnapshot.Create(
            Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(Fixture.ReadBytes("Catalog.xml"))
                    .Replace("정부24", displayName, StringComparison.Ordinal)),
            parser,
            "yourtablecloth/TableClothCatalog",
            commit,
            RetrievedAt.AddHours(hours));

    private sealed class QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"linuxcloth-catalog-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
            GC.SuppressFinalize(this);
        }
    }
}
