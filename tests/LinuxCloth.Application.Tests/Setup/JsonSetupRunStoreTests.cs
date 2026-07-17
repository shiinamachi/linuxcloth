using LinuxCloth.Application.Images;
using LinuxCloth.Application.Setup;

namespace LinuxCloth.Application.Tests.Setup;

public sealed class JsonSetupRunStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"linuxcloth-setup-run-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripsRunWithPrivateModesAndCanClearIt()
    {
        var store = new JsonSetupRunStore(_root);
        var run = CreateRun();

        await store.SaveAsync(run);
        var loaded = await store.LoadAsync();

        Assert.Equal(run, loaded);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_root));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_root, JsonSetupRunStore.FileName)));
        }

        await store.ClearAsync();
        Assert.Null(await store.LoadAsync());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":99}")]
    public async Task RejectsCorruptedOrUnsupportedRun(string contents)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, JsonSetupRunStore.FileName), contents);
        var store = new JsonSetupRunStore(_root);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task RejectsUnknownPropertiesAndRelativePaths()
    {
        var store = new JsonSetupRunStore(_root);
        await store.SaveAsync(CreateRun());
        var path = Path.Combine(_root, JsonSetupRunStore.FileName);
        var serialized = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, serialized[..^1] + ",\"password\":\"secret\"}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        var relative = CreateRun() with
        {
            Inputs = CreateRun().Inputs with { WindowsIsoPath = "relative/windows.iso" },
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(relative));
    }

    private static SetupRun CreateRun()
    {
        var now = DateTimeOffset.Parse("2026-07-17T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return new SetupRun(
            SetupRun.CurrentSchemaVersion,
            Guid.Parse("fa82bfd7-f5fd-45b3-9fb6-fde7d3d31e75"),
            SetupPhase.BuildingImage,
            new SetupInputSnapshot(
                "/media/windows.iso",
                new SetupFileFingerprint(1024, now, new string('a', 64)),
                "/media/virtio.iso",
                new SetupFileFingerprint(2048, now, new string('b', 64)),
                6,
                "Professional",
                "Windows 11 Pro",
                new string('c', 64),
                ImageId.Parse("windows-11"),
                80,
                4,
                8192,
                LicenseConfirmed: true),
            "/data/linuxcloth/images/.staging-windows-11",
            null,
            3,
            now,
            now.AddMinutes(1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
