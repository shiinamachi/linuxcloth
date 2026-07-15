using System.Security.Cryptography;
using System.Text;

namespace LinuxCloth.Application.ImageBuilding;

internal sealed class ImageBuildOperationLock : IDisposable
{
    private readonly FileStream _stream;

    private ImageBuildOperationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ImageBuildOperationLock Acquire(string lockRoot, string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        Directory.CreateDirectory(lockRoot);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                lockRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stagingDirectory)));
        var path = Path.Combine(lockRoot, $"{Convert.ToHexString(digest).ToLowerInvariant()}.lock");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                BufferSize = 1,
            };
            if (OperatingSystem.IsLinux())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            var stream = new FileStream(path, options);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new ImageBuildOperationLock(stream);
        }
        catch (IOException exception)
        {
            throw new WindowsImageBuildException(
                "Another process is already operating on this Windows image build.",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
