using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LinuxCloth.Application.Images;

internal static class ImageArtifactHasher
{
    private static readonly byte[] TreeHashDomain = "linuxcloth-swtpm-tree-v1\0"u8.ToArray();

    public static async Task<ManagedImageFileMetadata> HashFileAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        SecureImageFileSystem.EnsureRegularFile(path, description);
        var before = ReadFileSnapshot(path);

        byte[] digest;
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var after = ReadFileSnapshot(path);
        if (before != after)
        {
            throw new ImageRegistryException($"The {description} changed while it was being hashed.");
        }

        return new ManagedImageFileMetadata(
            Convert.ToHexString(digest).ToLowerInvariant(),
            before.Length,
            before.LastWriteUtcTicks);
    }

    public static async Task<ManagedImageTreeMetadata> HashTpmTreeAsync(
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        SecureImageFileSystem.EnsureNoReparsePointInExistingPath(rootDirectory);
        SecureImageFileSystem.EnsureDirectory(rootDirectory, "swtpm state template directory");

        var rootLastWriteUtcTicks = Directory.GetLastWriteTimeUtc(rootDirectory).Ticks;
        var entries = EnumerateTree(rootDirectory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(TreeHashDomain);
        Span<byte> rootTimestamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(rootTimestamp, rootLastWriteUtcTicks);
        hash.AppendData(rootTimestamp);

        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendEntryHeader(hash, entry);
                if (entry.IsDirectory)
                {
                    continue;
                }

                await using var stream = new FileStream(
                    entry.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer.AsSpan(0, bytesRead));
                }

                var after = ReadFileSnapshot(entry.FullPath);
                if (after.Length != entry.Length || after.LastWriteUtcTicks != entry.LastWriteUtcTicks)
                {
                    throw new ImageRegistryException(
                        $"An swtpm state file changed while it was being hashed: {entry.RelativePath}");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        foreach (var directory in entries.Where(entry => entry.IsDirectory))
        {
            var currentTicks = Directory.GetLastWriteTimeUtc(directory.FullPath).Ticks;
            if (currentTicks != directory.LastWriteUtcTicks)
            {
                throw new ImageRegistryException(
                    $"An swtpm state directory changed while it was being hashed: {directory.RelativePath}");
            }
        }

        if (Directory.GetLastWriteTimeUtc(rootDirectory).Ticks != rootLastWriteUtcTicks)
        {
            throw new ImageRegistryException("The swtpm state tree changed while it was being hashed.");
        }

        var files = entries.Where(entry => !entry.IsDirectory).ToArray();
        var lastWriteTicks = entries.Count == 0
            ? rootLastWriteUtcTicks
            : Math.Max(rootLastWriteUtcTicks, entries.Max(entry => entry.LastWriteUtcTicks));

        return new ManagedImageTreeMetadata(
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            files.Length,
            files.Sum(entry => entry.Length),
            lastWriteTicks);
    }

    private static List<TreeEntry> EnumerateTree(string rootDirectory)
    {
        var entries = new List<TreeEntry>();
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        long totalLength = 0;
        var fileCount = 0;

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entryPath);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ImageRegistryException(
                        $"The swtpm template cannot contain a symbolic link or reparse point: {entryPath}");
                }

                var relativePath = Path.GetRelativePath(rootDirectory, entryPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativeTreePath(relativePath);

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    entries.Add(new TreeEntry(
                        relativePath,
                        entryPath,
                        IsDirectory: true,
                        Length: 0,
                        Directory.GetLastWriteTimeUtc(entryPath).Ticks));
                    pending.Push(entryPath);
                }
                else
                {
                    var snapshot = ReadFileSnapshot(entryPath);
                    try
                    {
                        totalLength = checked(totalLength + snapshot.Length);
                    }
                    catch (OverflowException exception)
                    {
                        throw new ImageRegistryException("The swtpm state tree length overflowed.", exception);
                    }

                    fileCount++;
                    if (fileCount > ImageRegistryLimits.MaximumTpmFileCount ||
                        totalLength > ImageRegistryLimits.MaximumTpmTotalBytes)
                    {
                        throw new ImageRegistryException("The swtpm state tree exceeds its file or byte limit.");
                    }

                    entries.Add(new TreeEntry(
                        relativePath,
                        entryPath,
                        IsDirectory: false,
                        snapshot.Length,
                        snapshot.LastWriteUtcTicks));
                }

                if (entries.Count > ImageRegistryLimits.MaximumTpmEntryCount)
                {
                    throw new ImageRegistryException("The swtpm state tree exceeds its entry limit.");
                }
            }
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return entries;
    }

    private static void ValidateRelativeTreePath(string relativePath)
    {
        if (Encoding.UTF8.GetByteCount(relativePath) > ImageRegistryLimits.MaximumTpmRelativePathBytes)
        {
            throw new ImageRegistryException("An swtpm state path exceeds its UTF-8 byte limit.");
        }

        if (relativePath.Split('/').Length > ImageRegistryLimits.MaximumTpmTreeDepth)
        {
            throw new ImageRegistryException("The swtpm state tree exceeds its maximum depth.");
        }
    }

    private static void AppendEntryHeader(IncrementalHash hash, TreeEntry entry)
    {
        var pathBytes = Encoding.UTF8.GetBytes(entry.RelativePath);
        Span<byte> header = stackalloc byte[17];
        header[0] = entry.IsDirectory ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(header[1..5], pathBytes.Length);
        BinaryPrimitives.WriteInt64BigEndian(header[5..13], entry.Length);
        BinaryPrimitives.WriteInt32BigEndian(header[13..17], 0);
        hash.AppendData(header);
        hash.AppendData(pathBytes);

        Span<byte> timestamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timestamp, entry.LastWriteUtcTicks);
        hash.AppendData(timestamp);
    }

    private static FileSnapshot ReadFileSnapshot(string path)
    {
        SecureImageFileSystem.EnsureRegularFile(path, "managed image artifact");
        var info = new FileInfo(path);
        return new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private sealed record TreeEntry(
        string RelativePath,
        string FullPath,
        bool IsDirectory,
        long Length,
        long LastWriteUtcTicks);

    private readonly record struct FileSnapshot(long Length, long LastWriteUtcTicks);
}
