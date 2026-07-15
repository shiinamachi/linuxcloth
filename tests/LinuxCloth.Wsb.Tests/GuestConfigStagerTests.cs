using System.Security.Cryptography;
using System.Text;
using LinuxCloth.Core;

namespace LinuxCloth.Wsb.Tests;

public sealed class GuestConfigStagerTests
{
    [Fact]
    public async Task StagesCanonicalFilesAndByteCopiesTheCatalog()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var catalogBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Catalog>\r\n  exact bytes\n</Catalog>");
        var catalogPath = Path.Combine(temporaryDirectory.Path, "upstream-catalog.xml");
        await File.WriteAllBytesAsync(catalogPath, catalogBytes);
        var serviceIds = new[] { ServiceId.Parse("WooriBank"), ServiceId.Parse("KB") };
        var manifest = CreateManifest(serviceIds, catalogBytes);
        var wsb = ExpressWsbGenerator.Generate(serviceIds);
        var destination = Path.Combine(temporaryDirectory.Path, "config");

        await GuestConfigStager.StageAsync(destination, manifest, wsb, catalogPath);

        var expectedManifest = GuestLaunchManifestSerializer.SerializeToUtf8Bytes(manifest);
        var expectedHash = GuestLaunchManifestSerializer.ComputeSha256Hex(expectedManifest);
        Assert.Equal(expectedManifest, await File.ReadAllBytesAsync(Path.Combine(destination, "launch.json")));
        Assert.Equal(
            $"{expectedHash}  launch.json\n",
            await File.ReadAllTextAsync(Path.Combine(destination, "launch.json.sha256")));
        Assert.Equal(wsb, await File.ReadAllTextAsync(Path.Combine(destination, "express.wsb")));
        Assert.Equal(catalogBytes, await File.ReadAllBytesAsync(Path.Combine(destination, "Catalog.xml")));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".linuxcloth-config-*.tmp"));
    }

    [Fact]
    public async Task CatalogSnapshotIsOptional()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serviceIds = new[] { ServiceId.Parse("WooriBank") };
        var manifest = CreateManifest(serviceIds, Encoding.UTF8.GetBytes("not-copied"));
        var destination = Path.Combine(temporaryDirectory.Path, "config");

        await GuestConfigStager.StageAsync(
            destination,
            manifest,
            ExpressWsbGenerator.Generate(serviceIds));

        Assert.False(File.Exists(Path.Combine(destination, "Catalog.xml")));
        Assert.True(File.Exists(Path.Combine(destination, "launch.json")));
    }

    [Fact]
    public async Task HashMismatchLeavesNoPartiallyPublishedDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var catalogPath = Path.Combine(temporaryDirectory.Path, "Catalog.xml");
        await File.WriteAllBytesAsync(catalogPath, Encoding.UTF8.GetBytes("actual"));
        var serviceIds = new[] { ServiceId.Parse("WooriBank") };
        var manifest = CreateManifest(serviceIds, Encoding.UTF8.GetBytes("different"));
        var destination = Path.Combine(temporaryDirectory.Path, "config");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestConfigStager.StageAsync(
                destination,
                manifest,
                ExpressWsbGenerator.Generate(serviceIds),
                catalogPath));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".linuxcloth-config-*.tmp"));
    }

    [Fact]
    public async Task ManifestAndWsbMustDescribeTheSameSessionPolicy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var wsb = ExpressWsbGenerator.Generate([ServiceId.Parse("WooriBank")]);
        var manifest = CreateManifest([ServiceId.Parse("KB")], Encoding.UTF8.GetBytes("catalog"));
        var destination = Path.Combine(temporaryDirectory.Path, "config");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestConfigStager.StageAsync(destination, manifest, wsb));

        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task StagesAnExplicitDisabledNetworkPolicy()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serviceIds = new[] { ServiceId.Parse("WooriBank") };
        var manifest = new GuestLaunchManifest(
            Guid.NewGuid(),
            serviceIds,
            new string('a', 64),
            networkEnabled: false,
            clipboardEnabled: true,
            DateTimeOffset.UtcNow);
        var destination = Path.Combine(temporaryDirectory.Path, "config");

        await GuestConfigStager.StageAsync(
            destination,
            manifest,
            ExpressWsbGenerator.Generate(serviceIds, networkEnabled: false, clipboardEnabled: true));

        Assert.True(File.Exists(Path.Combine(destination, "launch.json")));
    }

    [Fact]
    public async Task CancellationLeavesNoPartiallyPublishedDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var serviceIds = new[] { ServiceId.Parse("WooriBank") };
        var manifest = CreateManifest(serviceIds, Encoding.UTF8.GetBytes("catalog"));
        var destination = Path.Combine(temporaryDirectory.Path, "config");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GuestConfigStager.StageAsync(
                destination,
                manifest,
                ExpressWsbGenerator.Generate(serviceIds),
                cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(temporaryDirectory.Path, ".linuxcloth-config-*.tmp"));
    }

    private static GuestLaunchManifest CreateManifest(
        IReadOnlyList<ServiceId> serviceIds,
        ReadOnlySpan<byte> catalogBytes) =>
        new(
            Guid.Parse("12345678-1234-5678-9abc-def012345678"),
            serviceIds,
            Convert.ToHexString(SHA256.HashData(catalogBytes)).ToLowerInvariant(),
            networkEnabled: true,
            clipboardEnabled: false,
            new DateTimeOffset(2026, 7, 15, 1, 2, 3, TimeSpan.Zero));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("linuxcloth-wsb-tests-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
