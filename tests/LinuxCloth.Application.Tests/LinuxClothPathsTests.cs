using LinuxCloth.Application.Storage;

namespace LinuxCloth.Application.Tests;

public sealed class LinuxClothPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lc-app-{Guid.NewGuid():N}");

    [Fact]
    public void UsesAbsoluteXdgLocations()
    {
        var environment = new Dictionary<string, string?>
        {
            ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(_root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(_root, "cache"),
            ["XDG_RUNTIME_DIR"] = Path.Combine(_root, "runtime"),
        };

        var paths = LinuxClothPaths.FromEnvironment(
            name => environment.GetValueOrDefault(name),
            Path.Combine(_root, "home"));

        Assert.Equal(Path.Combine(_root, "config", "linuxcloth"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(_root, "data", "linuxcloth"), paths.DataDirectory);
        Assert.Equal(Path.Combine(_root, "cache", "linuxcloth"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(_root, "runtime", "linuxcloth"), paths.RuntimeDirectory);
        Assert.Equal(
            Path.Combine(_root, "cache", "linuxcloth", "windows-media-analysis"),
            paths.WindowsMediaAnalysisDirectory);
    }

    [Fact]
    public void IgnoresRelativeXdgLocationsAndUsesSecureRuntimeFallback()
    {
        var paths = LinuxClothPaths.FromEnvironment(
            _ => "relative/path",
            Path.Combine(_root, "home"),
            Path.Combine(_root, "tmp"),
            unixUserId: 1234);

        Assert.Equal(Path.Combine(_root, "home", ".config", "linuxcloth"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(_root, "home", ".local", "share", "linuxcloth"), paths.DataDirectory);
        Assert.Equal(Path.Combine(_root, "home", ".cache", "linuxcloth"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(_root, "tmp", "linuxcloth-runtime-1234", "linuxcloth"), paths.RuntimeDirectory);
    }

    [Fact]
    public void CreatesPrivateApplicationDirectories()
    {
        var paths = LinuxClothPaths.FromEnvironment(
            _ => null,
            Path.Combine(_root, "home"),
            Path.Combine(_root, "tmp"),
            unixUserId: 42);

        paths.CreateBaseDirectories();

        Assert.True(Directory.Exists(paths.ImagesDirectory));
        Assert.True(Directory.Exists(paths.SessionsDirectory));
        Assert.True(Directory.Exists(paths.WindowsMediaAnalysisDirectory));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(paths.RuntimeDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(paths.WindowsMediaAnalysisDirectory));
        }
    }

    [Fact]
    public void RejectsSymbolicLinkApplicationDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var target = Path.Combine(_root, "target");
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateSymbolicLink(Path.Combine(dataRoot, "linuxcloth"), target);
        var paths = LinuxClothPaths.FromEnvironment(
            name => name == "XDG_DATA_HOME" ? dataRoot : null,
            Path.Combine(_root, "home"),
            Path.Combine(_root, "tmp"),
            unixUserId: 42);

        var exception = Assert.Throws<IOException>(paths.CreateBaseDirectories);

        Assert.Contains("symbolic-link", exception.Message, StringComparison.Ordinal);
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
