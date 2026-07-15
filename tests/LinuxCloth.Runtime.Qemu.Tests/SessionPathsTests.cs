using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SessionPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}");

    [Fact]
    public void CreatesPrivateSessionDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var paths = SessionPaths.Create(_root, Guid.NewGuid());

        paths.CreateDirectories();

        Assert.True(Directory.Exists(paths.ConfigDirectory));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(paths.SessionDirectory));
    }

    [Fact]
    public void RefusesRuntimeRootThatMakesSocketsTooLong()
    {
        var longRoot = Path.Combine(Path.GetTempPath(), new string('a', 100));

        Assert.Throws<PathTooLongException>(() => SessionPaths.Create(longRoot, Guid.NewGuid()));
    }

    [Fact]
    public void CleanerDoesNotFollowDirectorySymlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var paths = SessionPaths.Create(_root, Guid.NewGuid());
        paths.CreateDirectories();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateSymbolicLink(Path.Combine(paths.SessionDirectory, "link"), outside);

        SessionCleaner.Delete(paths);

        Assert.True(File.Exists(sentinel));
        Assert.False(Directory.Exists(paths.SessionDirectory));
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
