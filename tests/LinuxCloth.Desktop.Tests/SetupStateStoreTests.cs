using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class SetupStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"linuxcloth-setup-state-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripsRememberedMediaAndPrivateModes()
    {
        var store = new SetupStateStore(_root);
        var state = SetupState.Default with
        {
            LastStep = SetupStep.VirtioMedia,
            RememberMediaPaths = true,
            WindowsIsoPath = "/media/windows.iso",
            VirtioIsoPath = "/media/virtio.iso",
            StagingDirectory = "/data/images/.staging-windows",
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Equal(state, loaded);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(_root));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(_root, SetupStateStore.FileName)));
        }
    }

    [Fact]
    public async Task DoesNotPersistMediaPathsWithoutExplicitOptIn()
    {
        var store = new SetupStateStore(_root);
        var state = SetupState.Default with
        {
            WindowsIsoPath = "/media/windows.iso",
            VirtioIsoPath = "/media/virtio.iso",
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.Null(loaded.WindowsIsoPath);
        Assert.Null(loaded.VirtioIsoPath);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":99}")]
    public async Task CorruptedOrUnsupportedStateFallsBackToDefaults(string contents)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, SetupStateStore.FileName), contents);
        var store = new SetupStateStore(_root);

        var loaded = await store.LoadAsync();

        Assert.Equal(SetupState.Default, loaded);
    }

    [Fact]
    public async Task RejectsUnknownPropertiesAsCorruptedState()
    {
        var store = new SetupStateStore(_root);
        await store.SaveAsync(SetupState.Default);
        var path = Path.Combine(_root, SetupStateStore.FileName);
        var state = await File.ReadAllTextAsync(path);
        var withUnknownProperty = state[..^1] + ",\"credential\":\"secret\"}";
        await File.WriteAllTextAsync(path, withUnknownProperty);

        var loaded = await store.LoadAsync();

        Assert.Equal(SetupState.Default, loaded);
    }

    [Fact]
    public async Task RejectsRelativePathsWhenSaving()
    {
        var store = new SetupStateStore(_root);
        var state = SetupState.Default with { StagingDirectory = "relative/staging" };

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(state));
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
