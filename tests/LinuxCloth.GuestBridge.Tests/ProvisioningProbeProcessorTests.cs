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
        Assert.Equal(8, properties.Length);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Nonce, document.RootElement.GetProperty("nonce").GetString());
        Assert.Equal(hash, document.RootElement.GetProperty("guestBridgeSha256").GetString());
        Assert.Equal("1.0.0-test", document.RootElement.GetProperty("guestBridgeVersion").GetString());
        Assert.Equal("X64", document.RootElement.GetProperty("windowsArchitecture").GetString());
        Assert.Equal(26100, document.RootElement.GetProperty("windowsBuild").GetInt32());
        Assert.Equal("Professional", document.RootElement.GetProperty("windowsEditionId").GetString());
        Assert.Equal("24H2", document.RootElement.GetProperty("windowsDisplayVersion").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, $".{ProvisioningProbeProcessor.ResultFileName}.tmp-*"));
    }

    [Theory]
    [InlineData("empty-version")]
    [InlineData("control-version")]
    [InlineData("long-version")]
    [InlineData("wrong-architecture")]
    [InlineData("old-build")]
    [InlineData("empty-edition")]
    [InlineData("control-edition")]
    [InlineData("long-edition")]
    [InlineData("empty-display-version")]
    [InlineData("control-display-version")]
    [InlineData("long-display-version")]
    public async Task RejectsInvalidEnvironmentProvenance(string invalidCase)
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        WriteValidProbe(directory.Path, Hash(executablePath));
        var provenance = invalidCase switch
        {
            "empty-version" => FakeGuestEnvironmentProvider.ValidProvenance with { GuestBridgeVersion = string.Empty },
            "control-version" => FakeGuestEnvironmentProvider.ValidProvenance with { GuestBridgeVersion = "1.0\nmalformed" },
            "long-version" => FakeGuestEnvironmentProvider.ValidProvenance with { GuestBridgeVersion = new string('x', 129) },
            "wrong-architecture" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsArchitecture = "Arm64" },
            "old-build" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsBuild = 21999 },
            "empty-edition" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsEditionId = string.Empty },
            "control-edition" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsEditionId = "Professional\r\n" },
            "long-edition" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsEditionId = new string('x', 129) },
            "empty-display-version" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsDisplayVersion = string.Empty },
            "control-display-version" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsDisplayVersion = "24H2\t" },
            "long-display-version" => FakeGuestEnvironmentProvider.ValidProvenance with { WindowsDisplayVersion = new string('x', 65) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };
        var processor = new ProvisioningProbeProcessor(
            new FakeDriveProvider(directory.Path),
            new FakeExecutableProvider(executablePath),
            new FakeGuestEnvironmentProvider(provenance));

        var outcome = await processor.ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Invalid, outcome.Status);
        Assert.False(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    [Fact]
    public async Task RejectsEnvironmentProviderFailureWithoutWritingResult()
    {
        using var directory = new TemporaryDirectory();
        using var executable = new TemporaryDirectory();
        var executablePath = WriteExecutable(executable.Path);
        WriteValidProbe(directory.Path, Hash(executablePath));
        var processor = new ProvisioningProbeProcessor(
            new FakeDriveProvider(directory.Path),
            new FakeExecutableProvider(executablePath),
            new FakeGuestEnvironmentProvider(failure: new InvalidDataException("Invalid Windows provenance.")));

        var outcome = await processor.ProcessAsync(CancellationToken.None);

        Assert.Equal(ProvisioningProbeStatus.Invalid, outcome.Status);
        Assert.False(File.Exists(Path.Combine(directory.Path, ProvisioningProbeProcessor.ResultFileName)));
    }

    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static ProvisioningProbeProcessor CreateProcessor(
        string executablePath,
        params string[] roots) =>
        new(
            new FakeDriveProvider(roots),
            new FakeExecutableProvider(executablePath),
            new FakeGuestEnvironmentProvider());

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
