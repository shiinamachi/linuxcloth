using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

public sealed record GuestLaunchManifest
{
    public const int CurrentSchemaVersion = 1;

    public GuestLaunchManifest(
        Guid sessionId,
        IReadOnlyList<ServiceId> serviceIds,
        string catalogSha256,
        bool networkEnabled,
        bool clipboardEnabled,
        DateTimeOffset issuedAt)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("The session UUID cannot be empty.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(catalogSha256);
        if (catalogSha256.Length != 64 || !catalogSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The catalog SHA-256 must contain exactly 64 hexadecimal characters.", nameof(catalogSha256));
        }

        SchemaVersion = CurrentSchemaVersion;
        SessionId = sessionId;
        ServiceIds = Array.AsReadOnly(
            ServiceIdSet.ValidateAndCopy(serviceIds, allowEmpty: false, nameof(serviceIds)));
        CatalogSha256 = catalogSha256.ToLowerInvariant();
        NetworkEnabled = networkEnabled;
        ClipboardEnabled = clipboardEnabled;
        IssuedAtUtc = issuedAt.ToUniversalTime();
    }

    public int SchemaVersion { get; }

    public Guid SessionId { get; }

    public IReadOnlyList<ServiceId> ServiceIds { get; }

    public string CatalogSha256 { get; }

    public bool NetworkEnabled { get; }

    public bool ClipboardEnabled { get; }

    public DateTimeOffset IssuedAtUtc { get; }
}
