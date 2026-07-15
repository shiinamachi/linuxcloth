namespace LinuxCloth.Runtime.Qemu.Sessions;

public static class SessionCleaner
{
    public static void Delete(SessionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!Directory.Exists(paths.SessionDirectory))
        {
            return;
        }

        var sessionsRoot = Path.GetFullPath(Path.Combine(paths.RuntimeRoot, "sessions"));
        var sessionDirectory = Path.GetFullPath(paths.SessionDirectory);
        var relative = Path.GetRelativePath(sessionsRoot, sessionDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the linuxcloth sessions root.");
        }

        DeleteTreeWithoutFollowingLinks(new DirectoryInfo(sessionDirectory));
    }

    private static void DeleteTreeWithoutFollowingLinks(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                entry.Delete();
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteTreeWithoutFollowingLinks(childDirectory);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        directory.Delete();
    }
}

