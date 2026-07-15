namespace LinuxCloth.Runtime.Qemu.Sessions;

public interface IBootIdProvider
{
    string GetBootId();
}

public sealed class LinuxBootIdProvider : IBootIdProvider
{
    private const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    public string GetBootId()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("QEMU session persistence requires a Linux boot identifier.");
        }

        var value = File.ReadAllText(BootIdPath).Trim();
        if (!Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Linux boot identifier is not a canonical lowercase UUID.");
        }

        return value;
    }
}
