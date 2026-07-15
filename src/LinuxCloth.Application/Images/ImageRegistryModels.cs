using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Application.Images;

public enum RegistrationFailureBehavior
{
    PreserveStaging,
    DeleteStaging,
}

public sealed record ImageRegistrationStaging
{
    internal ImageRegistrationStaging(ImageId imageId, string directoryPath)
    {
        ImageId = imageId;
        DirectoryPath = directoryPath;
        BaseImagePath = Path.Combine(directoryPath, ManagedImageRegistry.BaseImageFileName);
        OvmfVariablesTemplatePath = Path.Combine(
            directoryPath,
            ManagedImageRegistry.OvmfVariablesTemplateFileName);
        SwtpmStateTemplateDirectory = Path.Combine(
            directoryPath,
            ManagedImageRegistry.SwtpmStateTemplateDirectoryName);
    }

    public ImageId ImageId { get; }

    public string DirectoryPath { get; }

    public string BaseImagePath { get; }

    public string OvmfVariablesTemplatePath { get; }

    public string SwtpmStateTemplateDirectory { get; }
}

public sealed record ManagedImageFileMetadata(
    string Sha256,
    long Length,
    long LastWriteUtcTicks);

public sealed record ExternalImageFileMetadata(
    string Path,
    string Sha256,
    long Length,
    long LastWriteUtcTicks);

public sealed record ManagedImageTreeMetadata(
    string Sha256,
    int FileCount,
    long TotalLength,
    long LastWriteUtcTicks);

public sealed record ManagedImageMetadata(
    int SchemaVersion,
    ImageId ImageId,
    Guid MachineId,
    DateTimeOffset CreatedAt,
    ManagedImageFileMetadata BaseImage,
    ExternalImageFileMetadata OvmfCode,
    ManagedImageFileMetadata OvmfVariablesTemplate,
    ManagedImageTreeMetadata SwtpmStateTemplate)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ManagedWindowsImage(
    ManagedImageMetadata Metadata,
    string DirectoryPath,
    string BaseImagePath,
    string OvmfVariablesTemplatePath,
    string SwtpmStateTemplateDirectory)
{
    public ImageId ImageId => Metadata.ImageId;

    public SessionImageDefinition ToSessionImageDefinition() =>
        new(
            ImageId.Value,
            Metadata.MachineId,
            BaseImagePath,
            Metadata.OvmfCode.Path,
            OvmfVariablesTemplatePath,
            SwtpmStateTemplateDirectory);
}

public sealed record ImageVerificationIssue(string Code, string Artifact, string Message);

public sealed record ImageVerificationResult(
    ImageId ImageId,
    IReadOnlyList<ImageVerificationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public static class ImageRegistryLimits
{
    public const int MaximumMetadataBytes = 64 * 1024;
    public const int MaximumTpmFileCount = 128;
    public const int MaximumTpmEntryCount = 256;
    public const int MaximumTpmTreeDepth = 8;
    public const int MaximumTpmRelativePathBytes = 512;
    public const long MaximumTpmTotalBytes = 32L * 1024 * 1024;
}
