using LinuxCloth.Application.Storage;

namespace LinuxCloth.Application.Images;

public sealed class ManagedImageRegistry
{
    public const string BaseImageFileName = "base.qcow2";
    public const string OvmfVariablesTemplateFileName = "ovmf-vars.template.fd";
    public const string SwtpmStateTemplateDirectoryName = "swtpm-state.template";
    public const string MetadataFileName = "metadata.json";

    private const string StagingPrefix = ".staging-";
    private const string MetadataTemporaryPrefix = ".metadata.tmp-";
    private readonly string _imagesDirectory;
    private readonly TimeProvider _timeProvider;

    public ManagedImageRegistry(LinuxClothPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!Path.IsPathFullyQualified(paths.ImagesDirectory))
        {
            throw new ArgumentException("The image registry directory must be absolute.", nameof(paths));
        }

        _imagesDirectory = Path.GetFullPath(paths.ImagesDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ImagesDirectory => _imagesDirectory;

    /// <summary>
    /// Creates an in-registry work area. Image builders must create the sparse qcow2 directly at
    /// <see cref="ImageRegistrationStaging.BaseImagePath"/>; the registry intentionally has no base-image copy API.
    /// </summary>
    public ImageRegistrationStaging CreateStaging(ImageId imageId)
    {
        ImageId.ValidateInitialized(imageId);
        EnsureRegistryRoot();
        EnsureImageDoesNotExist(imageId);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var stagingName = $"{StagingPrefix}{imageId.Value}-{Guid.NewGuid():N}";
            var stagingPath = Path.Combine(_imagesDirectory, stagingName);
            if (EntryExists(stagingPath))
            {
                continue;
            }

            Directory.CreateDirectory(stagingPath);
            if (Directory.EnumerateFileSystemEntries(stagingPath).Any())
            {
                throw new ImageRegistryException(
                    "A newly allocated image staging directory was unexpectedly non-empty.");
            }

            try
            {
                SecureImageFileSystem.EnsurePrivateDirectory(stagingPath);
                var staging = new ImageRegistrationStaging(imageId, stagingPath);
                SecureImageFileSystem.EnsurePrivateDirectory(staging.SwtpmStateTemplateDirectory);
                return staging;
            }
            catch
            {
                SecureImageFileSystem.DeleteTreeWithoutFollowingLinks(stagingPath);
                throw;
            }
        }

        throw new ImageRegistryException("Unable to allocate a unique managed-image staging directory.");
    }

    public ImageRegistrationStaging OpenStaging(ImageId imageId, string stagingDirectory)
    {
        ImageId.ValidateInitialized(imageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        EnsureRegistryRoot();

        var staging = new ImageRegistrationStaging(imageId, Path.GetFullPath(stagingDirectory));
        ValidateStagingIdentity(staging, requireDirectory: true);
        return staging;
    }

    public void AbandonStaging(ImageRegistrationStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        EnsureRegistryRoot();
        ValidateStagingIdentity(staging, requireDirectory: false);
        SecureImageFileSystem.DeleteTreeWithoutFollowingLinks(staging.DirectoryPath);
    }

    public async Task<ManagedWindowsImage> PromoteAsync(
        ImageRegistrationStaging staging,
        Guid machineId,
        string ovmfCodePath,
        RegistrationFailureBehavior failureBehavior = RegistrationFailureBehavior.PreserveStaging,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (!Enum.IsDefined(failureBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(failureBehavior));
        }

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ovmfCodePath);
            if (machineId == Guid.Empty)
            {
                throw new ArgumentException("The image machine identifier cannot be empty.", nameof(machineId));
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegistryRoot();
            ValidateStagingIdentity(staging, requireDirectory: true);
            EnsureImageDoesNotExist(staging.ImageId);
            RemovePreviousMetadataAttempt(staging.DirectoryPath);
            ValidateStagingLayout(staging);

            if (!Path.IsPathFullyQualified(ovmfCodePath))
            {
                throw new ImageRegistryException("The OVMF code path must be absolute.");
            }

            var normalizedOvmfCodePath = Path.GetFullPath(ovmfCodePath);
            SecureImageFileSystem.EnsureRegularFile(
                normalizedOvmfCodePath,
                "OVMF code image",
                requireAbsolute: true);

            var baseImage = await ImageArtifactHasher.HashFileAsync(
                    staging.BaseImagePath,
                    "base qcow2 image",
                    cancellationToken)
                .ConfigureAwait(false);
            var ovmfVariables = await ImageArtifactHasher.HashFileAsync(
                    staging.OvmfVariablesTemplatePath,
                    "OVMF variables template",
                    cancellationToken)
                .ConfigureAwait(false);
            var ovmfCode = await ImageArtifactHasher.HashFileAsync(
                    normalizedOvmfCodePath,
                    "OVMF code image",
                    cancellationToken)
                .ConfigureAwait(false);
            var swtpmState = await ImageArtifactHasher.HashTpmTreeAsync(
                    staging.SwtpmStateTemplateDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            var metadata = new ManagedImageMetadata(
                ManagedImageMetadata.CurrentSchemaVersion,
                staging.ImageId,
                machineId,
                _timeProvider.GetUtcNow().ToUniversalTime(),
                baseImage,
                new ExternalImageFileMetadata(
                    normalizedOvmfCodePath,
                    ovmfCode.Sha256,
                    ovmfCode.Length,
                    ovmfCode.LastWriteUtcTicks),
                ovmfVariables,
                swtpmState);

            await WriteMetadataAtomicallyAsync(staging.DirectoryPath, metadata, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            SecureImageFileSystem.SealTree(staging.DirectoryPath);
            var targetDirectory = GetImageDirectory(staging.ImageId);
            EnsureImageDoesNotExist(staging.ImageId);
            Directory.Move(staging.DirectoryPath, targetDirectory);

            return CreateManagedImage(metadata, targetDirectory);
        }
        catch (Exception originalException) when (
            failureBehavior == RegistrationFailureBehavior.DeleteStaging)
        {
            try
            {
                AbandonStaging(staging);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Managed image promotion failed and its staging area could not be removed.",
                    originalException,
                    cleanupException);
            }

            throw;
        }
    }

    public async Task<ManagedWindowsImage> LoadAsync(
        ImageId imageId,
        CancellationToken cancellationToken = default)
    {
        ImageId.ValidateInitialized(imageId);
        EnsureRegistryRoot();
        var directory = GetImageDirectory(imageId);
        SecureImageFileSystem.EnsureNoReparsePointInExistingPath(directory);
        SecureImageFileSystem.EnsureDirectory(directory, "managed image directory");
        ValidateManagedImageLayout(directory);

        var metadata = await ReadMetadataAsync(directory, cancellationToken).ConfigureAwait(false);
        if (metadata.ImageId != imageId)
        {
            throw new ImageMetadataValidationException(
                "The metadata imageId does not match its managed image directory.");
        }

        var image = CreateManagedImage(metadata, directory);
        ValidateManagedArtifactPresence(image);
        return image;
    }

    public async Task<IReadOnlyList<ManagedWindowsImage>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureRegistryRoot();
        var imageIds = new List<ImageId>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(_imagesDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry);
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ImageRegistryException(
                    $"The image registry cannot contain a symbolic link or reparse point: {entry}");
            }

            if (TryParseStagingName(name, out _))
            {
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new ImageRegistryException($"A staging entry is not a directory: {entry}");
                }

                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory) || !ImageId.TryParse(name, out var imageId))
            {
                throw new ImageRegistryException($"The image registry contains an unknown entry: {entry}");
            }

            imageIds.Add(imageId);
        }

        imageIds.Sort();
        var images = new List<ManagedWindowsImage>(imageIds.Count);
        foreach (var imageId in imageIds)
        {
            images.Add(await LoadAsync(imageId, cancellationToken).ConfigureAwait(false));
        }

        return images;
    }

    public async Task<ImageVerificationResult> VerifyAsync(
        ImageId imageId,
        CancellationToken cancellationToken = default)
    {
        ImageId.ValidateInitialized(imageId);
        ManagedWindowsImage image;
        try
        {
            image = await LoadAsync(imageId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ImageRegistryException)
        {
            return new ImageVerificationResult(
                imageId,
                [new ImageVerificationIssue("invalid-structure", "image", exception.Message)]);
        }

        var issues = new List<ImageVerificationIssue>();
        await VerifyFileAsync(
                image.BaseImagePath,
                "baseImage",
                image.Metadata.BaseImage,
                issues,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyFileAsync(
                image.OvmfVariablesTemplatePath,
                "ovmfVariablesTemplate",
                image.Metadata.OvmfVariablesTemplate,
                issues,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyFileAsync(
                image.Metadata.OvmfCode.Path,
                "ovmfCode",
                new ManagedImageFileMetadata(
                    image.Metadata.OvmfCode.Sha256,
                    image.Metadata.OvmfCode.Length,
                    image.Metadata.OvmfCode.LastWriteUtcTicks),
                issues,
                cancellationToken)
            .ConfigureAwait(false);
        await VerifyTreeAsync(image, issues, cancellationToken).ConfigureAwait(false);
        VerifySealedModes(image, issues);

        return new ImageVerificationResult(imageId, issues);
    }

    private static async Task VerifyFileAsync(
        string path,
        string artifact,
        ManagedImageFileMetadata expected,
        List<ImageVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        ManagedImageFileMetadata actual;
        try
        {
            actual = await ImageArtifactHasher.HashFileAsync(path, artifact, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ImageRegistryException)
        {
            issues.Add(new ImageVerificationIssue("unreadable", artifact, exception.Message));
            return;
        }

        CompareFileMetadata(artifact, expected, actual, issues);
    }

    private static async Task VerifyTreeAsync(
        ManagedWindowsImage image,
        List<ImageVerificationIssue> issues,
        CancellationToken cancellationToken)
    {
        ManagedImageTreeMetadata actual;
        try
        {
            actual = await ImageArtifactHasher.HashTpmTreeAsync(
                    image.SwtpmStateTemplateDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ImageRegistryException)
        {
            issues.Add(new ImageVerificationIssue("unreadable", "swtpmStateTemplate", exception.Message));
            return;
        }

        var expected = image.Metadata.SwtpmStateTemplate;
        if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
        {
            issues.Add(new ImageVerificationIssue(
                "hash-mismatch",
                "swtpmStateTemplate",
                "The swtpm state tree SHA-256 digest does not match its metadata."));
        }

        if (expected.FileCount != actual.FileCount || expected.TotalLength != actual.TotalLength)
        {
            issues.Add(new ImageVerificationIssue(
                "length-mismatch",
                "swtpmStateTemplate",
                "The swtpm state tree file count or byte length does not match its metadata."));
        }

        if (expected.LastWriteUtcTicks != actual.LastWriteUtcTicks)
        {
            issues.Add(new ImageVerificationIssue(
                "mtime-mismatch",
                "swtpmStateTemplate",
                "The swtpm state tree modification time does not match its metadata."));
        }
    }

    private static void CompareFileMetadata(
        string artifact,
        ManagedImageFileMetadata expected,
        ManagedImageFileMetadata actual,
        List<ImageVerificationIssue> issues)
    {
        if (!string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal))
        {
            issues.Add(new ImageVerificationIssue(
                "hash-mismatch",
                artifact,
                $"The {artifact} SHA-256 digest does not match its metadata."));
        }

        if (expected.Length != actual.Length)
        {
            issues.Add(new ImageVerificationIssue(
                "length-mismatch",
                artifact,
                $"The {artifact} byte length does not match its metadata."));
        }

        if (expected.LastWriteUtcTicks != actual.LastWriteUtcTicks)
        {
            issues.Add(new ImageVerificationIssue(
                "mtime-mismatch",
                artifact,
                $"The {artifact} modification time does not match its metadata."));
        }
    }

    private static void VerifySealedModes(
        ManagedWindowsImage image,
        List<ImageVerificationIssue> issues)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var expectedDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        foreach (var directory in EnumerateDirectories(image))
        {
            if (File.GetUnixFileMode(directory) != expectedDirectoryMode)
            {
                issues.Add(new ImageVerificationIssue(
                    "mode-mismatch",
                    "image",
                    $"A managed image directory is not mode 0700: {directory}"));
            }
        }

        foreach (var file in EnumerateManagedFiles(image))
        {
            if (File.GetUnixFileMode(file) != UnixFileMode.UserRead)
            {
                issues.Add(new ImageVerificationIssue(
                    "mode-mismatch",
                    "image",
                    $"A managed image asset is not mode 0400: {file}"));
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(ManagedWindowsImage image)
    {
        yield return image.DirectoryPath;
        foreach (var directory in Directory.EnumerateDirectories(
                     image.SwtpmStateTemplateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            yield return directory;
        }

        yield return image.SwtpmStateTemplateDirectory;
    }

    private static IEnumerable<string> EnumerateManagedFiles(ManagedWindowsImage image)
    {
        yield return image.BaseImagePath;
        yield return image.OvmfVariablesTemplatePath;
        yield return Path.Combine(image.DirectoryPath, MetadataFileName);
        foreach (var file in Directory.EnumerateFiles(
                     image.SwtpmStateTemplateDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private static void ValidateManagedArtifactPresence(ManagedWindowsImage image)
    {
        SecureImageFileSystem.EnsureRegularFile(image.BaseImagePath, "base qcow2 image");
        SecureImageFileSystem.EnsureRegularFile(
            image.OvmfVariablesTemplatePath,
            "OVMF variables template");
        SecureImageFileSystem.EnsureRegularFile(
            image.Metadata.OvmfCode.Path,
            "OVMF code image",
            requireAbsolute: true);
        SecureImageFileSystem.EnsureDirectory(
            image.SwtpmStateTemplateDirectory,
            "swtpm state template directory");
        SecureImageFileSystem.EnsureTreeContainsNoReparsePoints(
            image.SwtpmStateTemplateDirectory,
            ImageRegistryLimits.MaximumTpmEntryCount);
    }

    private static ManagedWindowsImage CreateManagedImage(
        ManagedImageMetadata metadata,
        string directory)
    {
        return new ManagedWindowsImage(
            metadata,
            directory,
            Path.Combine(directory, BaseImageFileName),
            Path.Combine(directory, OvmfVariablesTemplateFileName),
            Path.Combine(directory, SwtpmStateTemplateDirectoryName));
    }

    private static async Task<ManagedImageMetadata> ReadMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, MetadataFileName);
        SecureImageFileSystem.EnsureRegularFile(path, "managed image metadata");
        var before = new FileInfo(path);
        if (before.Length > ImageRegistryLimits.MaximumMetadataBytes)
        {
            throw new ImageMetadataValidationException("The image metadata exceeds its size limit.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(path);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new ImageMetadataValidationException("The image metadata changed while it was being read.");
        }

        return ImageMetadataSerializer.Parse(bytes);
    }

    private static async Task WriteMetadataAtomicallyAsync(
        string directory,
        ManagedImageMetadata metadata,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(directory, MetadataFileName);
        var temporaryPath = Path.Combine(directory, $"{MetadataTemporaryPrefix}{Guid.NewGuid():N}");
        var contents = ImageMetadataSerializer.Serialize(metadata);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                SecureImageFileSystem.SetStagingFileMode(temporaryPath);
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void EnsureRegistryRoot()
    {
        SecureImageFileSystem.EnsurePrivateDirectory(_imagesDirectory);
    }

    private string GetImageDirectory(ImageId imageId) => Path.Combine(_imagesDirectory, imageId.Value);

    private void EnsureImageDoesNotExist(ImageId imageId)
    {
        var target = GetImageDirectory(imageId);
        if (EntryExists(target))
        {
            throw new ImageRegistryException($"A managed image already exists: {imageId.Value}");
        }
    }

    private void ValidateStagingIdentity(ImageRegistrationStaging staging, bool requireDirectory)
    {
        ImageId.ValidateInitialized(staging.ImageId);
        var fullPath = Path.GetFullPath(staging.DirectoryPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!PathEquals(parent, _imagesDirectory) ||
            !IsStagingName(Path.GetFileName(fullPath), staging.ImageId))
        {
            throw new ImageRegistryException("The staging directory is not owned by this image registry.");
        }

        if (!PathEquals(staging.BaseImagePath, Path.Combine(fullPath, BaseImageFileName)) ||
            !PathEquals(
                staging.OvmfVariablesTemplatePath,
                Path.Combine(fullPath, OvmfVariablesTemplateFileName)) ||
            !PathEquals(
                staging.SwtpmStateTemplateDirectory,
                Path.Combine(fullPath, SwtpmStateTemplateDirectoryName)))
        {
            throw new ImageRegistryException("The staging directory uses unexpected artifact paths.");
        }

        if (requireDirectory)
        {
            SecureImageFileSystem.EnsureNoReparsePointInExistingPath(fullPath);
            SecureImageFileSystem.EnsureDirectory(fullPath, "managed image staging directory");
        }
    }

    private static bool IsStagingName(string name, ImageId imageId)
    {
        var prefix = $"{StagingPrefix}{imageId.Value}-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 32)
        {
            return false;
        }

        foreach (var character in name.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseStagingName(string name, out ImageId imageId)
    {
        imageId = default;
        const int separatorAndTokenLength = 33;
        if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal) ||
            name.Length <= StagingPrefix.Length + separatorAndTokenLength)
        {
            return false;
        }

        var identifierLength = name.Length - StagingPrefix.Length - separatorAndTokenLength;
        var identifier = name.Substring(StagingPrefix.Length, identifierLength);
        return ImageId.TryParse(identifier, out imageId) && IsStagingName(name, imageId);
    }

    private static void RemovePreviousMetadataAttempt(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            if (!string.Equals(name, MetadataFileName, StringComparison.Ordinal) &&
                !name.StartsWith(MetadataTemporaryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ImageRegistryException("A metadata staging entry is not a regular file.");
            }

            File.Delete(entry);
        }
    }

    private static void ValidateStagingLayout(ImageRegistrationStaging staging)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            BaseImageFileName,
            OvmfVariablesTemplateFileName,
            SwtpmStateTemplateDirectoryName,
        };
        ValidateExactTopLevelEntries(staging.DirectoryPath, expected);
        SecureImageFileSystem.EnsureRegularFile(staging.BaseImagePath, "base qcow2 image");
        SecureImageFileSystem.EnsureRegularFile(
            staging.OvmfVariablesTemplatePath,
            "OVMF variables template");
        SecureImageFileSystem.EnsureTreeContainsNoReparsePoints(
            staging.SwtpmStateTemplateDirectory,
            ImageRegistryLimits.MaximumTpmEntryCount);
    }

    private static void ValidateManagedImageLayout(string directory)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            BaseImageFileName,
            OvmfVariablesTemplateFileName,
            SwtpmStateTemplateDirectoryName,
            MetadataFileName,
        };
        ValidateExactTopLevelEntries(directory, expected);
    }

    private static void ValidateExactTopLevelEntries(
        string directory,
        HashSet<string> expectedNames)
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var name = Path.GetFileName(entry);
            if (!expectedNames.Contains(name))
            {
                throw new ImageRegistryException($"The managed image contains an unknown entry: {entry}");
            }

            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ImageRegistryException(
                    $"The managed image cannot contain a symbolic link or reparse point: {entry}");
            }

            actual.Add(name);
        }

        var missing = expectedNames.FirstOrDefault(name => !actual.Contains(name));
        if (missing is not null)
        {
            throw new ImageRegistryException($"The managed image is missing the required entry '{missing}'.");
        }
    }

    private static bool EntryExists(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, PathComparison));
    }

    private static bool PathEquals(string? left, string? right) =>
        string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
