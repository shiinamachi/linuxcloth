namespace LinuxCloth.Application.ImageBuilding;

internal static class ImageBuildPathGuard
{
    private const int MaximumPathCharacters = 4096;

    public static string RequireRegularFile(
        string path,
        string description,
        bool requireExecutable = false)
    {
        var fullPath = NormalizeAbsolute(path, description);
        RequireNoSymbolicLinkComponents(fullPath, description);

        if (!File.Exists(fullPath))
        {
            throw new WindowsImageBuildException($"The {description} does not exist: {fullPath}");
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new WindowsImageBuildException($"The {description} must be a regular file: {fullPath}");
        }

        if (requireExecutable && OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(fullPath);
            var executableBits = UnixFileMode.UserExecute |
                                 UnixFileMode.GroupExecute |
                                 UnixFileMode.OtherExecute;
            if ((mode & executableBits) == 0)
            {
                throw new WindowsImageBuildException($"The {description} is not executable: {fullPath}");
            }
        }

        return fullPath;
    }

    public static string NormalizeAbsolute(string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > MaximumPathCharacters || path.Any(char.IsControl))
        {
            throw new WindowsImageBuildException($"The {description} path is invalid.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new WindowsImageBuildException($"The {description} path must be absolute: {path}");
        }

        return Path.GetFullPath(path);
    }

    public static void RequireNoSymbolicLinkComponents(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ??
                   throw new WindowsImageBuildException($"The {description} path has no filesystem root.");
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!TryGetAttributes(current, out var attributes))
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new WindowsImageBuildException(
                    $"The {description} path cannot traverse a symbolic link or reparse point: {current}");
            }
        }
    }

    public static void DeleteTreeWithoutFollowingLinks(string path)
    {
        if (!TryGetAttributes(path, out var attributes))
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !attributes.HasFlag(FileAttributes.Directory))
        {
            File.Delete(path);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteTreeWithoutFollowingLinks(entry);
        }

        Directory.Delete(path);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
