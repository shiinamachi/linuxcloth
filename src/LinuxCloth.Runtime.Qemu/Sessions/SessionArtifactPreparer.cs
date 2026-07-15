using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed class SessionArtifactPreparer
{
    private readonly IProcessRunner _processRunner;

    public SessionArtifactPreparer(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task PrepareAsync(
        SessionPaths paths,
        SessionImageDefinition image,
        string qemuImgPath,
        string bubblewrapPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(image);
        ValidateImage(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(qemuImgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bubblewrapPath);

        paths.CreateDirectories();

        try
        {
            await CreateOverlayAsync(
                    paths,
                    image.BaseImagePath,
                    qemuImgPath,
                    bubblewrapPath,
                    cancellationToken)
                .ConfigureAwait(false);
            File.Copy(image.OvmfVariablesTemplatePath, paths.OvmfVariablesPath, overwrite: false);
            CopyDirectory(image.SwtpmStateTemplateDirectory, paths.SwtpmStateDirectory);
            ApplyPrivateModes(paths);
        }
        catch
        {
            SessionCleaner.Delete(paths);
            throw;
        }
    }

    private async Task CreateOverlayAsync(
        SessionPaths paths,
        string baseImagePath,
        string qemuImgPath,
        string bubblewrapPath,
        CancellationToken cancellationToken)
    {
        var command = new ProcessSpec(
            qemuImgPath,
            [
                "create",
                "-q",
                "-f", "qcow2",
                "-F", "qcow2",
                "-b", Path.GetFullPath(baseImagePath),
                paths.OverlayPath,
            ],
            paths.SessionDirectory,
            new Dictionary<string, string?> { ["LC_ALL"] = "C" });
        var result = await _processRunner.RunAsync(
            BubblewrapSessionToolConfinement.WrapQemuImg(
                command,
                bubblewrapPath,
                paths.SessionDirectory,
                baseImagePath),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"qemu-img failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    private static void ValidateImage(SessionImageDefinition image)
    {
        var files = new[]
        {
            image.BaseImagePath,
            image.OvmfCodePath,
            image.OvmfVariablesTemplatePath,
        };

        var missing = files.FirstOrDefault(path => !Path.IsPathFullyQualified(path) || !File.Exists(path));
        if (missing is not null)
        {
            throw new FileNotFoundException("A required image artifact is missing or is not an absolute path.", missing);
        }

        if (!Path.IsPathFullyQualified(image.SwtpmStateTemplateDirectory) ||
            !Directory.Exists(image.SwtpmStateTemplateDirectory))
        {
            throw new DirectoryNotFoundException($"The swtpm template directory is missing: {image.SwtpmStateTemplateDirectory}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
        }
    }

    private static void ApplyPrivateModes(SessionPaths paths)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(paths.SessionDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var file in Directory.EnumerateFiles(paths.SessionDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
