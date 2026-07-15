namespace LinuxCloth.Application.Images;

public readonly struct ImageId : IEquatable<ImageId>, IComparable<ImageId>
{
    public const int MaximumLength = 63;

    private readonly string? _value;

    private ImageId(string value)
    {
        _value = value;
    }

    public string Value => _value ?? throw new InvalidOperationException("An image identifier must be initialized.");

    public static ImageId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParse(value, out var imageId))
        {
            throw new FormatException(
                "An image identifier must contain 1 to 63 lowercase ASCII letters, digits, or interior hyphens.");
        }

        return imageId;
    }

    public static bool TryParse(string? value, out ImageId imageId)
    {
        imageId = default;
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
        {
            return false;
        }

        if (!IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }
        }

        imageId = new ImageId(value);
        return true;
    }

    public int CompareTo(ImageId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public bool Equals(ImageId other) => StringComparer.Ordinal.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is ImageId other && Equals(other);

    public override int GetHashCode() => _value is null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(ImageId left, ImageId right) => left.Equals(right);

    public static bool operator !=(ImageId left, ImageId right) => !left.Equals(right);

    public static bool operator <(ImageId left, ImageId right) => left.CompareTo(right) < 0;

    public static bool operator <=(ImageId left, ImageId right) => left.CompareTo(right) <= 0;

    public static bool operator >(ImageId left, ImageId right) => left.CompareTo(right) > 0;

    public static bool operator >=(ImageId left, ImageId right) => left.CompareTo(right) >= 0;

    internal static void ValidateInitialized(ImageId imageId)
    {
        if (imageId._value is null)
        {
            throw new ArgumentException("The image identifier must be initialized.", nameof(imageId));
        }
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
