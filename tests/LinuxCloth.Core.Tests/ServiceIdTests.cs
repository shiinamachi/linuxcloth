using LinuxCloth.Core;

namespace LinuxCloth.Core.Tests;

public sealed class ServiceIdTests
{
    [Theory]
    [InlineData("WooriBank")]
    [InlineData("bank-1")]
    [InlineData("bank_test.example")]
    public void ValidIdentifiersAreAccepted(string value)
    {
        Assert.Equal(value, ServiceId.Parse(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" bank")]
    [InlineData("bank other")]
    [InlineData("bank';Remove-Item")]
    [InlineData("은행")]
    public void UnsafeIdentifiersAreRejected(string value)
    {
        Assert.False(ServiceId.TryCreate(value, out _));
    }
}

