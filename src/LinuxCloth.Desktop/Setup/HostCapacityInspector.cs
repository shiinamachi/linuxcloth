namespace LinuxCloth.Desktop.Setup;

public sealed record HostCapacitySnapshot(
    long AvailableMemoryBytes,
    long AvailableDiskBytes,
    int LogicalProcessorCount,
    bool HasRecommendedMemory,
    bool HasMinimumDiskSpace)
{
    public static HostCapacitySnapshot Unknown { get; } = new(0, 0, 0, false, false);
}

public static class HostCapacityInspector
{
    public const long RecommendedMemoryBytes = 6L * 1024 * 1024 * 1024;
    public const long MinimumDiskBytes = 64L * 1024 * 1024 * 1024;

    public static HostCapacitySnapshot Inspect(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullPath = Path.GetFullPath(dataDirectory);
        var root = Path.GetPathRoot(fullPath) ??
                   throw new InvalidOperationException("데이터 디렉터리의 파일 시스템을 확인할 수 없습니다.");
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var disk = new DriveInfo(root).AvailableFreeSpace;
        return Evaluate(memory, disk, Environment.ProcessorCount);
    }

    public static HostCapacitySnapshot Evaluate(
        long availableMemoryBytes,
        long availableDiskBytes,
        int logicalProcessorCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(availableMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(availableDiskBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalProcessorCount);

        return new HostCapacitySnapshot(
            availableMemoryBytes,
            availableDiskBytes,
            logicalProcessorCount,
            availableMemoryBytes >= RecommendedMemoryBytes,
            availableDiskBytes >= MinimumDiskBytes);
    }
}
