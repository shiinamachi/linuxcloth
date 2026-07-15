using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class GuestReadyReporterTests
{
    [Fact]
    public async Task WritesExactlyOneCanonicalSessionBoundMessage()
    {
        var sessionId = Guid.Parse("12345678-9abc-4def-8123-456789abcdef");
        var stream = new MemoryStream();
        var reporter = new VirtioSerialGuestReadyReporter(() => stream);

        await reporter.ReportAsync(sessionId, CancellationToken.None);

        Assert.Equal(GuestReadyProtocol.CreateMessage(sessionId), stream.ToArray());
        Assert.Equal(@"\\.\org.linuxcloth.guestbridge.0", VirtioSerialGuestReadyReporter.DevicePath);
    }

}
