namespace LinuxCloth.Core.Tests;

public sealed class GuestReadyProtocolTests
{
    [Fact]
    public void RoundTripsOneCanonicalBoundedMessage()
    {
        var sessionId = Guid.Parse("12345678-9abc-4def-8123-456789abcdef");

        var message = GuestReadyProtocol.CreateMessage(sessionId);

        Assert.True(GuestReadyProtocol.TryParse(message, out var parsed));
        Assert.Equal(sessionId, parsed);
        Assert.Equal("linuxcloth-ready-v1 123456789abc4def8123456789abcdef\n"u8.ToArray(), message);
        Assert.True(message.Length <= GuestReadyProtocol.MaximumMessageBytes);
    }

    [Theory]
    [InlineData("linuxcloth-ready-v1 123456789ABC4DEF8123456789ABCDEF\n")]
    [InlineData("linuxcloth-ready-v2 123456789abc4def8123456789abcdef\n")]
    [InlineData("linuxcloth-ready-v1 123456789abc4def8123456789abcdef")]
    [InlineData("linuxcloth-ready-v1 00000000000000000000000000000000\n")]
    [InlineData("linuxcloth-ready-v1 123456789abc4def8123456789abcdef extra\n")]
    public void RejectsNonCanonicalOrExtendedMessages(string value)
    {
        Assert.False(
            GuestReadyProtocol.TryParse(System.Text.Encoding.ASCII.GetBytes(value), out _));
    }

    [Fact]
    public void RejectsEmptySessionIdentifierWhenFormatting()
    {
        Assert.Throws<ArgumentException>(() => GuestReadyProtocol.CreateMessage(Guid.Empty));
    }
}
