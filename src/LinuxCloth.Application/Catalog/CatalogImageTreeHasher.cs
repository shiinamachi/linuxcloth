using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace LinuxCloth.Application.Catalog;

internal static class CatalogImageTreeHasher
{
    private const int MaximumDirectoryCount = CatalogAssetStore.MaximumImageCount * 2;
    private const int MaximumRelativePathBytes = 1024;

    public static async Task<string> ComputeAsync(
        string imagesDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesDirectory);
        var root = Path.GetFullPath(imagesDirectory);
        EnsureDirectory(root);
        var files = EnumerateFiles(root);
        using var treeHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalLength = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.FullPath);
            if (!info.Exists || info.Length <= 0 || info.Length > CatalogAssetStore.MaximumImageBytes)
            {
                throw new CatalogWorkspaceException(
                    $"The pinned catalog image has an invalid size: {file.RelativePath}");
            }

            totalLength += info.Length;
            if (totalLength > CatalogAssetStore.MaximumTotalImageBytes)
            {
                throw new CatalogWorkspaceException("The pinned catalog image tree exceeds its byte limit.");
            }

            var lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            var hash = await HashFileAsync(file.FullPath, info.Length, cancellationToken)
                .ConfigureAwait(false);
            info.Refresh();
            if (!info.Exists || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != lastWriteTicks)
            {
                throw new CatalogWorkspaceException(
                    $"The pinned catalog image changed while it was hashed: {file.RelativePath}");
            }

            var manifestEntry = Encoding.UTF8.GetBytes(
                $"{file.RelativePath}\0{file.Length}\0{hash}\n");
            treeHash.AppendData(manifestEntry);
        }

        return Convert.ToHexString(treeHash.GetHashAndReset());
    }

    private static ImageFile[] EnumerateFiles(string root)
    {
        var result = new List<ImageFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        var directoryCount = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureDirectory(directory);
            directoryCount++;
            if (directoryCount > MaximumDirectoryCount)
            {
                throw new CatalogWorkspaceException("The pinned catalog image tree has too many directories.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new CatalogWorkspaceException(
                        $"The pinned catalog image tree contains a symbolic link: {path}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(path);
                    continue;
                }

                if (result.Count >= CatalogAssetStore.MaximumImageCount)
                {
                    throw new CatalogWorkspaceException("The pinned catalog image tree has too many files.");
                }

                var relativePath = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relativePath.Length == 0 ||
                    relativePath.Any(char.IsControl) ||
                    Encoding.UTF8.GetByteCount(relativePath) > MaximumRelativePathBytes ||
                    !string.Equals(Path.GetExtension(relativePath), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CatalogWorkspaceException(
                        $"The pinned catalog image path is invalid: {relativePath}");
                }

                var info = new FileInfo(path);
                result.Add(new ImageFile(relativePath, path, info.Length));
            }
        }

        return result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> HashFileAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total > expectedLength)
                {
                    throw new CatalogWorkspaceException("A pinned catalog image grew while it was hashed.");
                }

                hash.AppendData(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total != expectedLength)
        {
            throw new CatalogWorkspaceException("A pinned catalog image was truncated while it was hashed.");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void EnsureDirectory(string path)
    {
        var information = new DirectoryInfo(path);
        if (!information.Exists || information.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogWorkspaceException(
                $"The pinned catalog image directory is missing or unsafe: {path}");
        }
    }

    private sealed record ImageFile(string RelativePath, string FullPath, long Length);
}
