namespace LinuxCloth.Runtime.Qemu.Qemu;

public static class QemuOptionEscaper
{
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("QEMU option values cannot contain NUL or newline characters.", nameof(value));
        }

        return value.Replace(",", ",,", StringComparison.Ordinal);
    }
}

