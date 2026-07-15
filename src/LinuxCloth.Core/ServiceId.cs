using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LinuxCloth.Core;

public readonly partial record struct ServiceId
{
    private ServiceId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ServiceId Parse(string value)
    {
        if (!TryCreate(value, out var serviceId))
        {
            throw new FormatException("A service identifier must be 1-128 ASCII letters, digits, dots, underscores, or hyphens and must start with a letter or digit.");
        }

        return serviceId;
    }

    public static bool TryCreate(
        string? value,
        [NotNullWhen(true)] out ServiceId serviceId)
    {
        if (value is null || !ServiceIdPattern().IsMatch(value))
        {
            serviceId = default;
            return false;
        }

        serviceId = new ServiceId(value);
        return true;
    }

    public override string ToString() => Value ?? string.Empty;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdPattern();
}

