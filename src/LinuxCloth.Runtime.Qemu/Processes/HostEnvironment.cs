namespace LinuxCloth.Runtime.Qemu.Processes;

public static class HostEnvironment
{
    private static readonly string[] DesktopVariables =
    [
        "DISPLAY",
        "WAYLAND_DISPLAY",
        "XDG_RUNTIME_DIR",
        "DBUS_SESSION_BUS_ADDRESS",
        "XAUTHORITY",
    ];

    public static IReadOnlyDictionary<string, string?> Minimal() =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
        };

    public static IReadOnlyDictionary<string, string?> Desktop()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C.UTF-8",
        };

        foreach (var name in DesktopVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                result[name] = value;
            }
        }

        return result;
    }
}
