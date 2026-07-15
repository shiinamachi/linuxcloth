namespace LinuxCloth.Runtime.Qemu.Doctor;

public sealed record FirmwareImage(string Path, long SizeBytes);

public sealed record FirmwarePair(
    string DescriptorPath,
    FirmwareImage Executable,
    FirmwareImage NvramTemplate);

public enum FirmwareDiagnosticCode
{
    DescriptorDirectoryNotFound,
    DescriptorEnumerationFailed,
    DescriptorCountLimitExceeded,
    DescriptorReadFailed,
    DescriptorTooLarge,
    InvalidJson,
    InvalidDescriptor,
    UnsupportedInterface,
    UnsupportedMapping,
    UnsupportedTarget,
    MissingRequiredFeatures,
    UnsafeMappingPath,
    FirmwareFileMissing,
    FirmwareFileInvalid,
    NoCompatibleDescriptor,
}

public sealed record FirmwareDiagnostic(
    FirmwareDiagnosticCode Code,
    string Message,
    string? DescriptorPath = null);

public sealed record FirmwareResolution(
    FirmwarePair? Pair,
    IReadOnlyList<FirmwareDiagnostic> Diagnostics)
{
    public bool IsResolved => Pair is not null;
}
