namespace LinuxCloth.Runtime.Qemu.Sessions;

public static class SessionCleaner
{
    public static void Delete(SessionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var runtimeRootInfo = new DirectoryInfo(paths.RuntimeRoot);
        var sessionsRootInfo = new DirectoryInfo(Path.Combine(paths.RuntimeRoot, "sessions"));
        if (runtimeRootInfo.LinkTarget is not null || sessionsRootInfo.LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to clean sessions through a symbolic-link runtime root.");
        }

        var sessionInfo = new DirectoryInfo(paths.SessionDirectory);
        if (sessionInfo.LinkTarget is not null)
        {
            sessionInfo.Delete();
            return;
        }

        if (!sessionInfo.Exists)
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

        DeleteTreeWithoutFollowingLinks(sessionInfo);
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
