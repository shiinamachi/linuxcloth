namespace LinuxCloth.Core;

public enum CompatibilityStatus
{
    Untested,
    Verified,
    Partial,
    Blocked,
}

public sealed record CompatibilityRecord(
    ServiceId ServiceId,
    CompatibilityStatus Status,
    DisplayMode PreferredDisplay,
    string? TestedWindowsBuild,
    string? TestedSporkVersion,
    string? TestedCatalogCommit,
    string? TestedQemuVersion,
    IReadOnlyList<string> KnownIssues,
    DateOnly? LastVerifiedAt);

