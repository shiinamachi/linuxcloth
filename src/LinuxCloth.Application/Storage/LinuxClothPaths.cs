using System.Runtime.InteropServices;

namespace LinuxCloth.Application.Storage;

public sealed partial record LinuxClothPaths(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory,
    string RuntimeDirectory)
{
    private const string ApplicationDirectoryName = "linuxcloth";

    public string CatalogDirectory => Path.Combine(DataDirectory, "catalog");

    public string CompatibilityDirectory => Path.Combine(DataDirectory, "compatibility");

    public string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");

    public string ImagesDirectory => Path.Combine(DataDirectory, "images");

    public string SessionsDirectory => Path.Combine(RuntimeDirectory, "sessions");

    public static LinuxClothPaths FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static LinuxClothPaths FromEnvironment(
        Func<string, string?> getEnvironmentVariable,
        string homeDirectory,
        string? temporaryDirectory = null,
        uint? unixUserId = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        var home = Path.GetFullPath(homeDirectory);
        var configRoot = ResolveXdgRoot(getEnvironmentVariable("XDG_CONFIG_HOME"), Path.Combine(home, ".config"));
        var dataRoot = ResolveXdgRoot(getEnvironmentVariable("XDG_DATA_HOME"), Path.Combine(home, ".local", "share"));
        var cacheRoot = ResolveXdgRoot(getEnvironmentVariable("XDG_CACHE_HOME"), Path.Combine(home, ".cache"));
        var runtimeRoot = ResolveRuntimeRoot(
            getEnvironmentVariable("XDG_RUNTIME_DIR"),
            temporaryDirectory ?? Path.GetTempPath(),
            unixUserId);

        return new LinuxClothPaths(
            Path.Combine(configRoot, ApplicationDirectoryName),
            Path.Combine(dataRoot, ApplicationDirectoryName),
            Path.Combine(cacheRoot, ApplicationDirectoryName),
            Path.Combine(runtimeRoot, ApplicationDirectoryName));
    }

    public void CreateBaseDirectories()
    {
        CreatePrivateDirectory(ConfigDirectory);
        CreatePrivateDirectory(DataDirectory);
        CreatePrivateDirectory(CacheDirectory);
        CreatePrivateDirectory(RuntimeDirectory);
        CreatePrivateDirectory(CatalogDirectory);
        CreatePrivateDirectory(CompatibilityDirectory);
        CreatePrivateDirectory(DiagnosticsDirectory);
        CreatePrivateDirectory(ImagesDirectory);
        CreatePrivateDirectory(SessionsDirectory);
    }

    private static string ResolveXdgRoot(string? configuredPath, string fallbackPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathFullyQualified(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.GetFullPath(fallbackPath);
    }

    private static string ResolveRuntimeRoot(
        string? configuredPath,
        string temporaryDirectory,
        uint? unixUserId)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathFullyQualified(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var userId = unixUserId ?? GetEffectiveUserId();
        return Path.Combine(Path.GetFullPath(temporaryDirectory), $"linuxcloth-runtime-{userId}");
    }

    private static uint GetEffectiveUserId() =>
        OperatingSystem.IsLinux() ? NativeMethods.GetEffectiveUserId() : 0;

    private static void CreatePrivateDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException($"A file exists where linuxcloth requires a directory: {path}");
        }

        if (Directory.Exists(path) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"linuxcloth refuses to use a symbolic-link directory: {path}");
        }

        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "geteuid")]
        internal static partial uint GetEffectiveUserId();
    }
}
