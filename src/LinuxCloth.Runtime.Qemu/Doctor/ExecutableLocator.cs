namespace LinuxCloth.Runtime.Qemu.Doctor;

public interface IExecutableLocator
{
    string? Find(string executableName);
}

public sealed class ExecutableLocator : IExecutableLocator
{
    private readonly string _path;

    public ExecutableLocator(string? path = null)
    {
        _path = path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    }

    public string? Find(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (Path.IsPathFullyQualified(executableName))
        {
            return IsExecutable(executableName) ? Path.GetFullPath(executableName) : null;
        }

        foreach (var directory in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (IsExecutable(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}

