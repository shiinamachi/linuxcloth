namespace LinuxCloth.GuestBridge.Tests;

public sealed class BoundedFileDiagnosticLogTests
{
    [Fact]
    public void BoundsCurrentAndPreviousLogsAndWritesOnlyFixedEventNames()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "diagnostics.log");
        const int maximumBytes = 512;
        File.WriteAllBytes(path, new byte[maximumBytes + 1]);
        File.WriteAllBytes(path + ".1", new byte[maximumBytes + 1]);
        var log = new BoundedFileDiagnosticLog(path, maximumBytes);

        for (var index = 0; index < 100; index++)
        {
            log.Write(DiagnosticEvent.ConfigurationRejected);
        }

        Assert.InRange(new FileInfo(path).Length, 1, maximumBytes);
        Assert.InRange(new FileInfo(path + ".1").Length, 1, maximumBytes);
        Assert.DoesNotContain("?", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.All(
            File.ReadLines(path),
            line => Assert.EndsWith(
                DiagnosticEvent.ConfigurationRejected.ToString(),
                line,
                StringComparison.Ordinal));
    }
}
