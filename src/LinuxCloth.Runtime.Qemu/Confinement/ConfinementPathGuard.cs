namespace LinuxCloth.Runtime.Qemu.Confinement;

internal static class ConfinementPathGuard
{
    public static string RequireAbsolutePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (path.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(path) || ContainsTraversalSegment(path))
        {
            throw new ArgumentException("A normalized absolute path without control characters is required.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    public static bool ContainsTraversalSegment(string value)
    {
        for (var index = 0; index < value.Length - 1; index++)
        {
            if (value[index] != '.' || value[index + 1] != '.')
            {
                continue;
            }

            var startsSegment = index == 0 || IsPathSegmentBoundary(value[index - 1]);
            var endsSegment = index + 2 == value.Length || IsPathSegmentBoundary(value[index + 2]);
            if (startsSegment && endsSegment)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSameOrDescendant(string candidate, string expectedRoot)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot));
        return string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal) ||
               fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static void RequireExistingDirectoryWithoutLinks(string path, string parameterName)
    {
        RequireNoSymbolicLinkComponents(path, requireLeaf: true, parameterName);
        if (!Directory.Exists(path))
        {
            throw new ArgumentException("An existing directory is required.", parameterName);
        }
    }

    public static void RequireExistingRegularFileWithoutLinks(string path, string parameterName)
    {
        RequireNoSymbolicLinkComponents(path, requireLeaf: true, parameterName);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException("An existing regular file is required.", parameterName);
        }
    }

    public static void RequireNoSymbolicLinkComponents(
        string path,
        bool requireLeaf,
        string parameterName)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("A rooted path is required.", parameterName);
        var relativePath = fullPath[root.Length..];
        var components = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            var information = Directory.Exists(current)
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);
            information.Refresh();

            if (information.LinkTarget is not null ||
                (information.Exists && (information.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new ArgumentException($"Symbolic links are not accepted in confined paths: {current}", parameterName);
            }

            if (!information.Exists)
            {
                if (requireLeaf || index < components.Length - 1)
                {
                    throw new ArgumentException($"Confined path component does not exist: {current}", parameterName);
                }

                return;
            }
        }
    }

    private static bool IsPathSegmentBoundary(char value) => value is '/' or '\\' or '=' or ':' or ',';
}
