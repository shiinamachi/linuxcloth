using System.Text;

namespace LinuxCloth.Desktop.Setup;

public enum DistributionFamily
{
    Unsupported,
    Arch,
    Debian,
    Fedora,
}

public sealed record DistributionInfo(
    string Id,
    string? Name,
    string? VersionId,
    IReadOnlyList<string> IdLike,
    DistributionFamily Family,
    string SourcePath);

public sealed class DistributionInfoReader
{
    public const int MaximumOsReleaseBytes = 64 * 1024;

    private readonly string[] _candidatePaths;

    public DistributionInfoReader(params string[]? candidatePaths)
    {
        _candidatePaths = candidatePaths is { Length: > 0 }
            ? candidatePaths.Select(Path.GetFullPath).ToArray()
            : ["/etc/os-release", "/usr/lib/os-release"];
    }

    public async Task<DistributionInfo> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var sourcePath = _candidatePaths.FirstOrDefault(File.Exists) ??
                         throw new FileNotFoundException("Linux 배포판 정보를 찾지 못했습니다.");
        var file = new FileInfo(sourcePath);
        if (file.Length <= 0 || file.Length > MaximumOsReleaseBytes)
        {
            throw new InvalidDataException("os-release 파일 크기가 허용 범위를 벗어났습니다.");
        }

        var contents = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return Parse(contents, sourcePath);
    }

    public static DistributionInfo Parse(string contents, string sourcePath = "/etc/os-release")
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (Encoding.UTF8.GetByteCount(contents) > MaximumOsReleaseBytes)
        {
            throw new InvalidDataException("os-release 데이터가 허용 크기를 초과했습니다.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidDataException("os-release 항목 형식이 올바르지 않습니다.");
            }

            var key = trimmed[..separator];
            if (!IsValidKey(key) || !values.TryAdd(key, ParseValue(trimmed[(separator + 1)..])))
            {
                throw new InvalidDataException($"os-release 항목 '{key}'이(가) 유효하지 않거나 중복되었습니다.");
            }
        }

        if (!values.TryGetValue("ID", out var id) || !IsValidIdentifier(id))
        {
            throw new InvalidDataException("os-release에 유효한 ID가 없습니다.");
        }

        var idLike = values.GetValueOrDefault("ID_LIKE")?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant())
            .Where(IsValidIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var normalizedId = id.ToLowerInvariant();
        return new DistributionInfo(
            normalizedId,
            NullIfEmpty(values.GetValueOrDefault("NAME")),
            NullIfEmpty(values.GetValueOrDefault("VERSION_ID")),
            idLike,
            ResolveFamily(normalizedId, idLike),
            Path.GetFullPath(sourcePath));
    }

    private static string ParseValue(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (value[0] == '\'')
        {
            if (value.Length < 2 || value[^1] != '\'')
            {
                throw new InvalidDataException("os-release 작은따옴표 값이 닫히지 않았습니다.");
            }

            return value[1..^1];
        }

        if (value[0] == '"')
        {
            if (value.Length < 2 || value[^1] != '"')
            {
                throw new InvalidDataException("os-release 큰따옴표 값이 닫히지 않았습니다.");
            }

            var result = new StringBuilder(value.Length - 2);
            for (var index = 1; index < value.Length - 1; index++)
            {
                var character = value[index];
                if (character != '\\')
                {
                    result.Append(character);
                    continue;
                }

                if (++index >= value.Length - 1)
                {
                    throw new InvalidDataException("os-release 이스케이프가 완전하지 않습니다.");
                }

                var escaped = value[index];
                if (escaped is '"' or '\\' or '$' or '`')
                {
                    result.Append(escaped);
                }
                else
                {
                    result.Append('\\').Append(escaped);
                }
            }

            return result.ToString();
        }

        if (value.Any(char.IsWhiteSpace) || value.Contains('"') || value.Contains('\''))
        {
            throw new InvalidDataException("os-release 따옴표 없는 값에 허용되지 않는 문자가 있습니다.");
        }

        return value;
    }

    private static DistributionFamily ResolveFamily(string id, IReadOnlyList<string> idLike)
    {
        if (string.Equals(id, "arch", StringComparison.Ordinal) ||
            idLike.Contains("arch", StringComparer.Ordinal))
        {
            return DistributionFamily.Arch;
        }

        string[] debianIds = ["debian", "ubuntu", "linuxmint", "pop", "elementary"];
        string[] fedoraIds = ["fedora", "rhel", "centos", "rocky", "almalinux"];
        if (debianIds.Contains(id, StringComparer.Ordinal) ||
            idLike.Any(value => debianIds.Contains(value, StringComparer.Ordinal)))
        {
            return DistributionFamily.Debian;
        }

        return fedoraIds.Contains(id, StringComparer.Ordinal) ||
               idLike.Any(value => fedoraIds.Contains(value, StringComparer.Ordinal))
            ? DistributionFamily.Fedora
            : DistributionFamily.Unsupported;
    }

    private static bool IsValidKey(string key) =>
        key.Length > 0 &&
        key[0] is >= 'A' and <= 'Z' or '_' &&
        key.All(static character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool IsValidIdentifier(string value) =>
        value.Length is > 0 and <= 64 &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
