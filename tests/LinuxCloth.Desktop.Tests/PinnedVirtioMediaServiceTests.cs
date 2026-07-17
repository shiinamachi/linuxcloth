using System.Net;
using System.Security.Cryptography;
using System.Text;
using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class PinnedVirtioMediaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcv{Guid.NewGuid():N}"[..9]);

    [Fact]
    public async Task BundledManifestPinsReviewedImmutableArtifact()
    {
        var artifact = await new PinnedVirtioManifestSource().ReadAsync();

        Assert.Equal(PinnedVirtioManifestSource.ArtifactId, artifact.Id);
        Assert.Equal("0.1.285-1", artifact.Version);
        Assert.Equal(789_645_312, artifact.Length);
        Assert.Equal("e14cf2b94492c3e925f0070ba7fdfedeb2048c91eea9c5a5afb30232a3976331", artifact.Sha256);
        var url = Assert.Single(artifact.Urls);
        Assert.Contains("/archive-virtio/virtio-win-0.1.285-1/", url.AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("latest", url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stable", url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vioscsi/w11/amd64", artifact.RequiredPaths);
        Assert.Contains("NetKVM/w11/amd64", artifact.RequiredPaths);
    }

    [Fact]
    public async Task DownloadsVerifiesAndReusesPrivateCache()
    {
        var body = Encoding.UTF8.GetBytes("pinned-virtio-test");
        var manifest = WriteManifest(body);
        var handler = new RecordingHandler(_ => Content(body));
        using var httpClient = new HttpClient(handler);
        using var service = new PinnedVirtioMediaService(
            Path.Combine(_root, "cache"),
            new PinnedVirtioManifestSource(manifest),
            httpClient);
        var progress = new List<VirtioMediaDownloadProgress>();

        var downloaded = await service.PrepareAsync(new ImmediateProgress(progress.Add));
        var cached = await service.PrepareAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(downloaded, cached);
        Assert.Equal(body, await File.ReadAllBytesAsync(downloaded.Path));
        Assert.Contains(progress, item => item.BytesReceived == body.Length);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(downloaded.Path)!, "*.tmp"));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(downloaded.Path));
        }
    }

    [Fact]
    public async Task DigestMismatchIsNeverPromotedToCache()
    {
        var expected = Encoding.UTF8.GetBytes("expected-payload");
        var actual = Encoding.UTF8.GetBytes("modified-payload");
        var manifest = WriteManifest(expected);
        using var httpClient = new HttpClient(new RecordingHandler(_ => Content(actual)));
        using var service = new PinnedVirtioMediaService(
            Path.Combine(_root, "cache"),
            new PinnedVirtioManifestSource(manifest),
            httpClient);

        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync());

        Assert.Empty(
            Directory.Exists(Path.Combine(_root, "cache"))
                ? Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*", SearchOption.AllDirectories)
                : []);
    }

    [Fact]
    public async Task RedirectOutsideAllowlistIsRejected()
    {
        var body = Encoding.UTF8.GetBytes("redirect-test");
        var manifest = WriteManifest(body);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/virtio-win.iso") },
        });
        using var httpClient = new HttpClient(handler);
        using var service = new PinnedVirtioMediaService(
            Path.Combine(_root, "cache"),
            new PinnedVirtioManifestSource(manifest),
            httpClient);

        var exception = await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync());

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task MutableManifestUrlIsRejectedBeforeNetworkAccess()
    {
        var body = Encoding.UTF8.GetBytes("mutable-test");
        var manifest = WriteManifest(
            body,
            "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new PinnedVirtioManifestSource(manifest).ReadAsync());
    }

    [Fact]
    public async Task CacheEscapingVersionIsRejected()
    {
        var body = Encoding.UTF8.GetBytes("unsafe-version");
        var manifest = WriteManifest(body);
        var contents = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(
            manifest,
            contents.Replace("test-version", "../outside", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new PinnedVirtioManifestSource(manifest).ReadAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteManifest(byte[] body, string? url = null)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "manifest.json");
        var sha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        File.WriteAllText(
            path,
            $$"""
              {
                "schemaVersion": 1,
                "artifacts": [
                  {
                    "id": "virtio-win-w11-amd64",
                    "version": "test-version",
                    "urls": [
                      "{{url ?? "https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-test-version/virtio-win.iso"}}"
                    ],
                    "length": {{body.Length}},
                    "sha256": "{{sha256}}",
                    "requiredPaths": [
                      "vioscsi/w11/amd64",
                      "NetKVM/w11/amd64"
                    ]
                  }
                ]
              }
              """);
        return path;
    }

    private static HttpResponseMessage Content(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class ImmediateProgress(Action<VirtioMediaDownloadProgress> report)
        : IProgress<VirtioMediaDownloadProgress>
    {
        public void Report(VirtioMediaDownloadProgress value) => report(value);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(respond(request));
        }
    }
}
