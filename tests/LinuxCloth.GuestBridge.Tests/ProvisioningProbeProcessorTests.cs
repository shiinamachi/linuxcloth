using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class ProvisioningProbeProcessorTests
{
    [Fact]
    public async Task RejectsDuplicateProperties()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        var hash = Hash(executablePath);
        WriteRawProbe(
            directory.Path,
            $$"""{"schemaVersion":1,"schemaVersion":1,"nonce":"{{Nonce}}","expectedGuestBridgeSha256":"{{hash}}"}""");

        var outcome = await CreateProcessor(executablePath, directory.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Invalid, outcome.Status);
        Assert.False(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task RejectsUnknownProperties()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        var hash = Hash(executablePath);
        WriteRawProbe(
            directory.Path,
            $$"""{"schemaVersion":1,"nonce":"{{Nonce}}","expectedGuestBridgeSha256":"{{hash}}","command":"calc.exe"}""");

        var outcome = await CreateProcessor(executablePath, directory.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Invalid, outcome.Status);
    }

    [Fact]
    public async Task RejectsOversizedProbeBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        var probePath = Path.Combine(directory.Path, ProvisioningProbeProcessor.ProbeFileName);
        using (var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(ProvisioningProbeProcessor.MaximumProbeBytes + 1);
        }

        var outcome = await CreateProcessor(executablePath, directory.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Invalid, outcome.Status);
    }

    [Fact]
    public async Task RejectsGuestBridgeHashMismatchWithoutWritingResult()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        WriteValidProbe(directory.Path, new string('0', 64));

        var outcome = await CreateProcessor(executablePath, directory.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.GuestBridgeHashMismatch, outcome.Status);
        Assert.False(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task RejectsMultipleProbeDrivesAsAmbiguous()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        var hash = Hash(executablePath);
        WriteValidProbe(first.Path, hash);
        WriteValidProbe(second.Path, hash);

        var outcome = await CreateProcessor(executablePath, first.Path, second.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Ambiguous, outcome.Status);
        Assert.False(File.Exists(Path.Combine(first.Path, ProvisioningProbeProcessor.ResultFileName)));
        Assert.False(File.Exists(Path.Combine(second.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task WritesCanonicalResultForExactlyOneValidMatchingProbe()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        var hash = Hash(executablePath);
        WriteValidProbe(directory.Path, hash);

        var outcome = await CreateProcessor(executablePath, directory.Path)
            .ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Success, outcome.Status);
        var resultPath = Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
        var properties = document.RootElement.EnumerateObject().ToArray();
        Assert.Equal(3, properties.Length);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Nonce, document.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(hash, document.RootElement.GetProperty("guestBridgeSha256").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, $".{ProvisioningProbeProcessor.ResultFileName}.tmp-*"));
    }

    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static ProvisioningProbeProcessor CreateProcessor(
        string executablePath,
        params string[] roots) =>
        new(new FakeDriveProvider(roots), new FakeExecutableProvider(executablePath));

    private static string WriteExecutable(string root)
    {
        var path = Path.Combine(root, "linuxcloth-guest-bridge.exe");
        File.WriteAllBytes(path, "single-file-guest-bridge"u8.ToArray());
        return path;
    }

    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void WriteValidProbe(string root, string expectedHash) =>
        WriteRawProbe(
            root,
            $$"""{"schemaVersion":1,"nonce":"{{Nonce}}","expectedGuestBridgeSha256":"{{expectedHash}}"}""");

    private static void WriteRawProbe(string root, string json) =>
        File.WriteAllText(
            Path.Combine(root, ProvisioningProbeProcessor.ProbeFileName),
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
