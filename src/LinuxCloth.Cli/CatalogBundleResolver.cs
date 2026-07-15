using LinuxCloth.Application.Catalog;

namespace LinuxCloth.Cli;

public sealed class CatalogBundleResolver
{
    public const string CatalogRootEnvironmentVariable = "LINUXCLOTH_CATALOG_ROOT";

    private readonly string _applicationBaseDirectory;
    private readonly string _currentDirectory;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public CatalogBundleResolver(
        Func<string, string?>? getEnvironmentVariable = null,
        string? currentDirectory = null,
        string? applicationBaseDirectory = null)
    {
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _currentDirectory = Path.GetFullPath(currentDirectory ?? Directory.GetCurrentDirectory());
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
    }

    public OfficialCatalogBundle Resolve(string? commandLineRoot) =>
        ResolveWithPolicy(commandLineRoot).Bundle;

    public CatalogBundleResolution ResolveWithPolicy(string? commandLineRoot)
    {
        var configured = commandLineRoot;
        if (configured is null)
        {
            var fromEnvironment = _getEnvironmentVariable(CatalogRootEnvironmentVariable);
            configured = string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
        }

        if (configured is not null)
        {
            if (string.IsNullOrWhiteSpace(configured) || configured.Length > 4096 || configured.Any(char.IsControl))
            {
                throw new CatalogBundleResolutionException("공식 카탈로그 루트 경로가 올바르지 않습니다.");
            }

            return new CatalogBundleResolution(
                ResolveConfiguredRoot(Path.GetFullPath(configured, _currentDirectory)),
                CatalogBundleUsePolicy.RequiredOverride);
        }

        var installed = Path.Combine(_applicationBaseDirectory, "catalog");
        if (File.Exists(Path.Combine(installed, "Catalog.xml")))
        {
            ValidateDocsDirectory(installed);
            return new CatalogBundleResolution(
                CreateFromDocsDirectory(installed),
                CatalogBundleUsePolicy.BundledFallback);
        }

        var root = FindDevelopmentCheckout(_currentDirectory) ??
                   FindDevelopmentCheckout(_applicationBaseDirectory);
        if (root is null)
        {
            throw new CatalogBundleResolutionException(
                $"공식 카탈로그를 찾지 못했습니다. --catalog-root 또는 {CatalogRootEnvironmentVariable}을 지정하세요.");
        }

        ValidateCheckout(root);
        return new CatalogBundleResolution(
            OfficialCatalogBundle.FromPinnedCheckout(root),
            CatalogBundleUsePolicy.BundledFallback);
    }

    private static OfficialCatalogBundle ResolveConfiguredRoot(string root)
    {
        if (File.Exists(Path.Combine(root, "docs", "Catalog.xml")))
        {
            ValidateCheckout(root);
            return OfficialCatalogBundle.FromPinnedCheckout(root);
        }

        ValidateDocsDirectory(root);
        return CreateFromDocsDirectory(root);
    }

    private static OfficialCatalogBundle CreateFromDocsDirectory(string directory) =>
        OfficialCatalogBundle.FromPinnedDocsDirectory(directory);

    private static string? FindDevelopmentCheckout(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
        {
            var solution = Path.Combine(current.FullName, "linuxcloth.slnx");
            var candidate = Path.Combine(current.FullName, "vendor", "TableClothCatalog");
            if (File.Exists(solution) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void ValidateCheckout(string root)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new CatalogBundleResolutionException("공식 카탈로그 루트는 절대 경로여야 합니다.");
        }

        var normalizedRoot = Path.GetFullPath(root);
        EnsureDirectory(normalizedRoot, "공식 카탈로그 루트");
        EnsureNoReparsePoints(normalizedRoot);

        var docs = Path.Combine(normalizedRoot, "docs");
        ValidateDocsDirectory(docs);
    }

    private static void ValidateDocsDirectory(string docs)
    {
        var normalizedDocs = Path.GetFullPath(docs);
        if (!Path.IsPathFullyQualified(normalizedDocs))
        {
            throw new CatalogBundleResolutionException("공식 카탈로그 docs 경로는 절대 경로여야 합니다.");
        }

        EnsureNoReparsePoints(normalizedDocs);
        var images = Path.Combine(normalizedDocs, "images");
        var catalog = Path.Combine(normalizedDocs, "Catalog.xml");
        EnsureDirectory(normalizedDocs, "공식 카탈로그 docs 디렉터리");
        EnsureDirectory(images, "공식 카탈로그 이미지 디렉터리");
        EnsureRegularFile(catalog, "공식 Catalog.xml");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists &&
                current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new CatalogBundleResolutionException(
                    $"심볼릭 링크를 통과하는 카탈로그 경로는 사용할 수 없습니다: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static void EnsureDirectory(string path, string description)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogBundleResolutionException($"{description}을 찾을 수 없거나 안전하지 않습니다: {path}");
        }
    }

    private static void EnsureRegularFile(string path, string description)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CatalogBundleResolutionException($"{description}을 찾을 수 없거나 안전하지 않습니다: {path}");
        }
    }
}

public sealed record CatalogBundleResolution(
    OfficialCatalogBundle Bundle,
    CatalogBundleUsePolicy UsePolicy);

public enum CatalogBundleUsePolicy
{
    BundledFallback,
    RequiredOverride,
}

public sealed class CatalogBundleResolutionException : Exception
{
    public CatalogBundleResolutionException(string message)
        : base(message)
    {
    }
}
