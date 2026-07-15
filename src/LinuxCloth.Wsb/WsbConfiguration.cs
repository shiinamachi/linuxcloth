using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

public sealed record WsbConfiguration
{
    public WsbConfiguration(
        WsbFeatureState networking = WsbFeatureState.Default,
        WsbFeatureState virtualGpu = WsbFeatureState.Default,
        int? memoryInMiB = null,
        WsbFeatureState clipboardRedirection = WsbFeatureState.Default,
        string? logonCommand = null,
        IReadOnlyList<WsbMappedFolder>? mappedFolders = null)
        : this(
            networking,
            virtualGpu,
            memoryInMiB,
            clipboardRedirection,
            logonCommand,
            mappedFolders,
            expressServiceIds: null)
    {
    }

    internal WsbConfiguration(
        WsbFeatureState networking,
        WsbFeatureState virtualGpu,
        int? memoryInMiB,
        WsbFeatureState clipboardRedirection,
        string? logonCommand,
        IReadOnlyList<WsbMappedFolder>? mappedFolders,
        IReadOnlyList<ServiceId>? expressServiceIds)
    {
        ValidateFeatureState(networking, nameof(networking));
        ValidateFeatureState(virtualGpu, nameof(virtualGpu));
        ValidateFeatureState(clipboardRedirection, nameof(clipboardRedirection));

        if (memoryInMiB is < 2048 or > 262144)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memoryInMiB),
                "Memory must be between 2048 MiB and 256 GiB when specified.");
        }

        if (logonCommand is not null &&
            (logonCommand.Length == 0 ||
             logonCommand.Length > 32768 ||
             logonCommand.Contains('\0')))
        {
            throw new ArgumentException("The logon command is empty, invalid, or too long.", nameof(logonCommand));
        }

        Networking = networking;
        VirtualGpu = virtualGpu;
        MemoryInMiB = memoryInMiB;
        ClipboardRedirection = clipboardRedirection;
        LogonCommand = logonCommand;
        var copiedMappedFolders = mappedFolders?.ToArray() ?? [];
        if (copiedMappedFolders.Any(folder => folder is null))
        {
            throw new ArgumentException("Mapped folders cannot contain null values.", nameof(mappedFolders));
        }

        MappedFolders = Array.AsReadOnly(copiedMappedFolders);
        ExpressServiceIds = expressServiceIds is null
            ? null
            : Array.AsReadOnly(expressServiceIds.ToArray());
    }

    public WsbFeatureState Networking { get; }

    public WsbFeatureState VirtualGpu { get; }

    public int? MemoryInMiB { get; }

    public WsbFeatureState ClipboardRedirection { get; }

    public string? LogonCommand { get; }

    public IReadOnlyList<WsbMappedFolder> MappedFolders { get; }

    public IReadOnlyList<ServiceId>? ExpressServiceIds { get; }

    public bool IsValidatedExpress => ExpressServiceIds is not null;

    private static void ValidateFeatureState(WsbFeatureState state, string parameterName)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(parameterName, state, "Unknown WSB feature state.");
        }
    }
}
