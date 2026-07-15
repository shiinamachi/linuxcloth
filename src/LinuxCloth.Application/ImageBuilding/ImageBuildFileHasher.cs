using System.Buffers;
using System.Security.Cryptography;

namespace LinuxCloth.Application.ImageBuilding;

internal static class ImageBuildFileHasher
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<ImageBuildFileFingerprint> HashAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        var fullPath = ImageBuildPathGuard.RequireRegularFile(path, description);
        var before = new FileInfo(fullPath);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }

            var after = new FileInfo(fullPath);
            if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                throw new WindowsImageBuildException($"The {description} changed while it was being verified.");
            }

            return new ImageBuildFileFingerprint(
                fullPath,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                after.Length,
                after.LastWriteTimeUtc.Ticks);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
