namespace LinuxCloth.Application.Images;

internal static class SecureImageFileSystem
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode StagingFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode SealedFileMode = UnixFileMode.UserRead;

    public static void EnsurePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        EnsureNoReparsePointInExistingPath(fullPath);

        if (File.Exists(fullPath))
        {
            throw new ImageRegistryException($"A file exists where a directory is required: {fullPath}");
        }

        Directory.CreateDirectory(fullPath);
        EnsureDirectory(fullPath, "image registry directory");
        SetPrivateDirectoryMode(fullPath);
    }

    public static void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new ImageRegistryException($"The {description} does not exist: {path}");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ImageRegistryException($"The {description} cannot be a symbolic link or reparse point: {path}");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ImageRegistryException($"The {description} is not a directory: {path}");
        }
    }

    public static void EnsureRegularFile(string path, string description, bool requireAbsolute = false)
    {
        if (requireAbsolute && !Path.IsPathFullyQualified(path))
        {
            throw new ImageRegistryException($"The {description} path must be absolute: {path}");
        }

        var fullPath = Path.GetFullPath(path);
        EnsureNoReparsePointInExistingPath(fullPath);
        if (!File.Exists(fullPath))
        {
            throw new ImageRegistryException($"The {description} does not exist: {fullPath}");
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ImageRegistryException($"The {description} must be a regular file: {fullPath}");
        }
    }

    public static void EnsureNoReparsePointInExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ImageRegistryException("The path has no filesystem root.");
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
                throw new ImageRegistryException(
                    $"Image registry paths cannot traverse a symbolic link or reparse point: {current}");
            }
        }
    }

    public static void SetStagingFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, StagingFileMode);
        }
    }

    public static void SealTree(string rootDirectory)
    {
        EnsureDirectory(rootDirectory, "managed image directory");

        var directories = new List<string> { rootDirectory };
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootDirectory);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ImageRegistryException(
                        $"Managed image assets cannot contain a symbolic link or reparse point: {entry}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    directories.Add(entry);
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (var file in files)
        {
            File.SetUnixFileMode(file, SealedFileMode);
        }

        foreach (var directory in directories)
        {
            File.SetUnixFileMode(directory, PrivateDirectoryMode);
        }
    }

    public static void EnsureTreeContainsNoReparsePoints(string rootDirectory, int maximumEntries)
    {
        EnsureDirectory(rootDirectory, "managed image tree");
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        var entryCount = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entryCount++;
                if (entryCount > maximumEntries)
                {
                    throw new ImageRegistryException("The managed image tree exceeds its entry limit.");
                }

                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ImageRegistryException(
                        $"Managed image assets cannot contain a symbolic link or reparse point: {entry}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    public static void DeleteTreeWithoutFollowingLinks(string rootDirectory)
    {
        if (!TryGetAttributes(rootDirectory, out var rootAttributes))
        {
            return;
        }

        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            DeleteEntry(rootDirectory, rootAttributes);
            return;
        }

        if (!rootAttributes.HasFlag(FileAttributes.Directory))
        {
            File.Delete(rootDirectory);
            return;
        }

        DeleteDirectoryContents(rootDirectory);
        Directory.Delete(rootDirectory);
    }

    private static void DeleteDirectoryContents(string directory)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, PrivateDirectoryMode);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                DeleteEntry(entry, attributes);
            }
            else if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteDirectoryContents(entry);
                Directory.Delete(entry);
            }
            else
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(entry, StagingFileMode);
                }

                File.Delete(entry);
            }
        }
    }

    private static void DeleteEntry(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
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

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }
}
