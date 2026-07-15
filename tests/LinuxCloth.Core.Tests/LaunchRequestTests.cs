using LinuxCloth.Core;

namespace LinuxCloth.Core.Tests;

public sealed class LaunchRequestTests
{
    [Fact]
    public void DefaultsAreSecure()
    {
        var request = new LaunchRequest([ServiceId.Parse("WooriBank")]);

        Assert.Equal(DisplayMode.Spice, request.DisplayMode);
        Assert.True(request.NetworkEnabled);
        Assert.False(request.ClipboardEnabled);
        Assert.Empty(request.UsbDeviceIds);
    }

    [Fact]
    public void DuplicateServicesAreRejected()
    {
        var id = ServiceId.Parse("WooriBank");

        Assert.Throws<ArgumentException>(() => new LaunchRequest([id, id]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void InvalidCpuCountIsRejected(int cpuCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LaunchRequest([ServiceId.Parse("WooriBank")], cpuCount: cpuCount));
    }
}

