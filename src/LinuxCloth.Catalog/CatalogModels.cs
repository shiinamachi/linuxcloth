using LinuxCloth.Core;

namespace LinuxCloth.Catalog;

public enum CatalogCategory
{
    Other,
    Banking,
    Financing,
    Security,
    Insurance,
    CreditCard,
    Government,
    Education,
}

public sealed record CatalogPackage(
    string Name,
    Uri Url,
    string? Arguments);

public sealed record CatalogEdgeExtension(
    string Name,
    Uri CrxUrl,
    string? ExtensionId);

public sealed record CatalogService(
    ServiceId Id,
    string DisplayName,
    string? EnglishDisplayName,
    CatalogCategory Category,
    Uri Url,
    string? CompatNotes,
    string? EnglishCompatNotes,
    IReadOnlyList<string> SearchKeywords,
    IReadOnlyList<CatalogPackage> Packages,
    IReadOnlyList<CatalogEdgeExtension> EdgeExtensions,
    string? CustomBootstrap)
{
    public bool HasCustomBootstrap => CustomBootstrap is not null;
}

public enum CatalogDiagnosticCode
{
    DuplicateServiceId,
}

public sealed record CatalogDiagnostic(
    CatalogDiagnosticCode Code,
    string Message,
    ServiceId? ServiceId);

public sealed record CatalogDocument(
    string? FallbackLanguage,
    IReadOnlyList<CatalogService> Services,
    IReadOnlyList<CatalogDiagnostic> Diagnostics);

public enum CatalogDuplicateIdPolicy
{
    Reject,
    KeepFirst,
}
