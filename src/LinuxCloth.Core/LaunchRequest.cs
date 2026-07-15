namespace LinuxCloth.Core;

public sealed record LaunchRequest
{
    public LaunchRequest(
        IReadOnlyList<ServiceId> serviceIds,
        int cpuCount = 4,
        int memoryMiB = 6144,
        DisplayMode displayMode = DisplayMode.Spice,
        bool networkEnabled = true,
        bool clipboardEnabled = false,
        IReadOnlyList<string>? usbDeviceIds = null)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);

        if (serviceIds.Count == 0)
        {
            throw new ArgumentException("At least one service must be selected.", nameof(serviceIds));
        }

        if (serviceIds.Count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(serviceIds), "At most 32 services can share one session.");
        }

        if (serviceIds.Distinct().Count() != serviceIds.Count)
        {
            throw new ArgumentException("Service identifiers must be unique.", nameof(serviceIds));
        }

        if (cpuCount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuCount), "CPU count must be between 1 and 64.");
        }

        if (memoryMiB is < 4096 or > 262144)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMiB), "Memory must be between 4096 MiB and 256 GiB.");
        }

        ServiceIds = serviceIds.ToArray();
        CpuCount = cpuCount;
        MemoryMiB = memoryMiB;
        DisplayMode = displayMode;
        NetworkEnabled = networkEnabled;
        ClipboardEnabled = clipboardEnabled;
        UsbDeviceIds = usbDeviceIds?.ToArray() ?? [];
    }

    public IReadOnlyList<ServiceId> ServiceIds { get; }

    public int CpuCount { get; }

    public int MemoryMiB { get; }

    public DisplayMode DisplayMode { get; }

    public bool NetworkEnabled { get; }

    public bool ClipboardEnabled { get; }

    public IReadOnlyList<string> UsbDeviceIds { get; }
}

