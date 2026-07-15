using System.Text.RegularExpressions;

namespace LinuxCloth.Desktop.Setup;

public sealed record PackagePlan(
    DistributionFamily Family,
    IReadOnlyList<string> RuntimePackages,
    IReadOnlyList<string> ImageBuildPackages,
    IReadOnlyList<string> AllPackages,
    string ManualInstallCommand);

public interface IPackageManifestSource
{
    Task<string> ReadAsync(
        DistributionFamily family,
        bool imageBuild,
        CancellationToken cancellationToken = default);
}

public sealed class FilePackageManifestSource : IPackageManifestSource
{
    private readonly string _directory;

    public FilePackageManifestSource(string? directory = null)
    {
        _directory = Path.GetFullPath(
            directory ?? Path.Combine(AppContext.BaseDirectory, "setup-packages"));
    }

    public Task<string> ReadAsync(
        DistributionFamily family,
        bool imageBuild,
        CancellationToken cancellationToken = default)
    {
        var familyDirectory = family switch
        {
            DistributionFamily.Debian => "deb",
            DistributionFamily.Fedora => "rpm",
            _ => throw new NotSupportedException("지원되지 않는 배포판에는 패키지 매니페스트가 없습니다."),
        };
        var fileName = imageBuild
            ? "image-build-dependencies.txt"
            : "runtime-dependencies.txt";
        var path = Path.Combine(_directory, familyDirectory, fileName);
        return File.ReadAllTextAsync(path, cancellationToken);
    }
}

public sealed partial class PackagePlanResolver
{
    private readonly IPackageManifestSource _source;

    public PackagePlanResolver(IPackageManifestSource? source = null)
    {
        _source = source ?? new FilePackageManifestSource();
    }

    public async Task<PackagePlan> ResolveAsync(
        DistributionInfo distribution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        if (distribution.Family == DistributionFamily.Unsupported)
        {
            throw new NotSupportedException(
                $"배포판 '{distribution.Id}'은 자동 패키지 계획을 지원하지 않습니다.");
        }

        var runtime = ParseManifest(
            await _source.ReadAsync(distribution.Family, imageBuild: false, cancellationToken)
                .ConfigureAwait(false));
        var imageBuild = ParseManifest(
            await _source.ReadAsync(distribution.Family, imageBuild: true, cancellationToken)
                .ConfigureAwait(false));
        var all = runtime.Concat(imageBuild).Distinct(StringComparer.Ordinal).ToArray();
        var command = distribution.Family == DistributionFamily.Debian
            ? $"sudo apt install -- {string.Join(' ', all)}"
            : $"sudo dnf install -- {string.Join(' ', all)}";
        return new PackagePlan(distribution.Family, runtime, imageBuild, all, command);
    }

    public static IReadOnlyList<string> ParseManifest(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var packages = new List<string>();
        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            if (!PackageNamePattern().IsMatch(value))
            {
                throw new InvalidDataException($"패키지 매니페스트에 유효하지 않은 이름이 있습니다: {value}");
            }

            packages.Add(value);
        }

        return packages.Distinct(StringComparer.Ordinal).ToArray();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();
}
