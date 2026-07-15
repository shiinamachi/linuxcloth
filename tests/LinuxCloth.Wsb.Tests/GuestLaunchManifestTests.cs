using System.Security.Cryptography;
using System.Text;
using LinuxCloth.Core;

namespace LinuxCloth.Wsb.Tests;

public sealed class GuestLaunchManifestTests
{
    private static readonly Guid SessionId = Guid.Parse("12345678-1234-5678-9abc-def012345678");

    [Fact]
    public void SerializationAndHashAreCanonicalAndDeterministic()
    {
        var manifest = CreateManifest();
        const string expected = "{\"schemaVersion\":1,\"sessionId\":\"12345678-1234-5678-9abc-def012345678\",\"serviceIds\":[\"WooriBank\",\"KB\"],\"catalogSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"networkEnabled\":true,\"clipboardEnabled\":false,\"issuedAt\":\"2026-07-15T01:11:12.1234567Z\"}";

        var first = GuestLaunchManifestSerializer.SerializeToUtf8Bytes(manifest);
        var second = GuestLaunchManifestSerializer.SerializeToUtf8Bytes(manifest);

        Assert.Equal(Encoding.UTF8.GetBytes(expected), first);
        Assert.Equal(first, second);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant(),
            GuestLaunchManifestSerializer.ComputeSha256Hex(first));
    }

    [Fact]
    public void CanonicalManifestRoundTrips()
    {
        var original = CreateManifest();

        var parsed = GuestLaunchManifestSerializer.Deserialize(
            GuestLaunchManifestSerializer.SerializeToUtf8Bytes(original));

        Assert.Equal(original.SchemaVersion, parsed.SchemaVersion);
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.ServiceIds, parsed.ServiceIds);
        Assert.Equal(original.CatalogSha256, parsed.CatalogSha256);
        Assert.Equal(original.NetworkEnabled, parsed.NetworkEnabled);
        Assert.Equal(original.ClipboardEnabled, parsed.ClipboardEnabled);
        Assert.Equal(original.IssuedAtUtc, parsed.IssuedAtUtc);
    }

    [Fact]
    public void ConstructorRejectsInvalidIdentityAndCatalogValues()
    {
        var validIds = new[] { ServiceId.Parse("WooriBank") };

        Assert.Throws<ArgumentException>(() =>
            new GuestLaunchManifest(Guid.Empty, validIds, new string('a', 64), true, false, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new GuestLaunchManifest(SessionId, validIds, "not-a-hash", true, false, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new GuestLaunchManifest(SessionId, [validIds[0], validIds[0]], new string('a', 64), true, false, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new GuestLaunchManifest(SessionId, [default], new string('a', 64), true, false, DateTimeOffset.UtcNow));
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void DeserializationRejectsNonCanonicalOrUnsafeManifests(string json)
    {
        Assert.Throws<LaunchManifestValidationException>(() =>
            GuestLaunchManifestSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));
    }

    public static TheoryData<string> InvalidManifests =>
        new()
        {
            ValidJson.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            ValidJson.Replace("\"WooriBank\"", "\"bank';Remove-Item\"", StringComparison.Ordinal),
            ValidJson.Replace(new string('a', 64), "bad", StringComparison.Ordinal),
            ValidJson.Replace("2026-07-15T01:11:12.1234567Z", "2026-07-15T01:11:12Z", StringComparison.Ordinal),
            ValidJson.Replace("\"issuedAt\":", "\"extra\":1,\"issuedAt\":", StringComparison.Ordinal),
            ValidJson.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"schemaVersion\":1,", StringComparison.Ordinal),
            ValidJson.Replace("\"networkEnabled\":true,", string.Empty, StringComparison.Ordinal),
        };

    private const string ValidJson = "{\"schemaVersion\":1,\"sessionId\":\"12345678-1234-5678-9abc-def012345678\",\"serviceIds\":[\"WooriBank\"],\"catalogSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"networkEnabled\":true,\"clipboardEnabled\":false,\"issuedAt\":\"2026-07-15T01:11:12.1234567Z\"}";

    private static GuestLaunchManifest CreateManifest() =>
        new(
            SessionId,
            [ServiceId.Parse("WooriBank"), ServiceId.Parse("KB")],
            new string('A', 64),
            networkEnabled: true,
            clipboardEnabled: false,
            new DateTimeOffset(2026, 7, 15, 10, 11, 12, TimeSpan.FromHours(9)).AddTicks(1_234_567));
}
