using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class HostCapacityInspectorTests
{
    [Theory]
    [InlineData(6, 64, true, true)]
    [InlineData(5, 64, false, true)]
    [InlineData(6, 63, true, false)]
    public void SeparatesMemoryAndDiskRecommendations(
        long memoryGiB,
        long diskGiB,
        bool expectedMemory,
        bool expectedDisk)
    {
        const long gibibyte = 1024L * 1024 * 1024;

        var result = HostCapacityInspector.Evaluate(memoryGiB * gibibyte, diskGiB * gibibyte, 8);

        Assert.Equal(expectedMemory, result.HasRecommendedMemory);
        Assert.Equal(expectedDisk, result.HasMinimumDiskSpace);
        Assert.Equal(8, result.LogicalProcessorCount);
    }
}
