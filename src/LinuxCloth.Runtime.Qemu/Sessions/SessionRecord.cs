using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed record SessionRecord
{
    public const int CurrentSchemaVersion = 1;

    public SessionRecord(
        Guid sessionId,
        string bootId,
        SessionState state,
        string imageId,
        string baseSha256,
        IEnumerable<ServiceId> serviceIds,
        IReadOnlyDictionary<string, ProcessIdentity>? processes = null,
        int schemaVersion = CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);

        SchemaVersion = schemaVersion;
        SessionId = sessionId;
        BootId = bootId;
        State = state;
        ImageId = imageId;
        BaseSha256 = baseSha256;
        ServiceIds = serviceIds.ToArray();
        Processes = processes is null
            ? new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal)
            : new Dictionary<string, ProcessIdentity>(processes, StringComparer.Ordinal);

        SessionRecordValidation.Validate(this);
    }

    public int SchemaVersion { get; }

    public Guid SessionId { get; }

    public string BootId { get; }

    public SessionState State { get; }

    public string ImageId { get; }

    public string BaseSha256 { get; }

    public IReadOnlyList<ServiceId> ServiceIds { get; }

    public IReadOnlyDictionary<string, ProcessIdentity> Processes { get; }
}

public static class SessionProcessNames
{
    public const string Qemu = "qemu";
    public const string Swtpm = "swtpm";
    public const string Passt = "passt";
    public const string Viewer = "viewer";

    internal static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Qemu,
        Swtpm,
        Passt,
        Viewer,
    };
}

internal static class SessionRecordValidation
{
    public const int MaximumRecordBytes = 64 * 1024;
    public const int MaximumServiceIds = 32;
    public const int MaximumProcesses = 4;
    public const int MaximumImageIdCharacters = 128;
    public const int MaximumExecutableCharacters = 4096;
    private const int MaximumLinuxProcessId = 4_194_304;

    public static void Validate(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.SchemaVersion != SessionRecord.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(record),
                record.SchemaVersion,
                $"Only session record schema version {SessionRecord.CurrentSchemaVersion} is supported.");
        }

        if (record.SessionId == Guid.Empty)
        {
            throw new ArgumentException("The session identifier cannot be empty.", nameof(record));
        }

        ValidateBootId(record.BootId, nameof(record));
        if (!Enum.IsDefined(record.State))
        {
            throw new ArgumentOutOfRangeException(nameof(record), record.State, "The session state is unknown.");
        }

        ValidateImageId(record.ImageId);
        ValidateSha256(record.BaseSha256);
        ValidateServices(record.ServiceIds);
        ValidateProcesses(record.BootId, record.Processes);
        if (record.State is SessionState.StartingVm or SessionState.WaitingForGuest or SessionState.Running &&
            !record.Processes.ContainsKey(SessionProcessNames.Qemu))
        {
            throw new ArgumentException(
                $"A session in state {record.State} must persist its QEMU process identity.",
                nameof(record));
        }
    }

    private static void ValidateBootId(string bootId, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootId, parameterName);
        if (!Guid.TryParseExact(bootId, "D", out var parsed) ||
            !string.Equals(bootId, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new ArgumentException("The boot identifier must be a canonical lowercase UUID.", parameterName);
        }
    }

    private static void ValidateImageId(string imageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        if (imageId.Length > MaximumImageIdCharacters ||
            !IsAsciiLetterOrDigit(imageId[0]) ||
            imageId.Any(static character =>
                !IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException(
                $"The image identifier must be 1-{MaximumImageIdCharacters} safe ASCII characters.",
                nameof(imageId));
        }
    }

    private static void ValidateSha256(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 || sha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The base image SHA-256 must contain exactly 64 lowercase hexadecimal characters.", nameof(sha256));
        }
    }

    private static void ValidateServices(IReadOnlyList<ServiceId> serviceIds)
    {
        if (serviceIds.Count is < 1 or > MaximumServiceIds)
        {
            throw new ArgumentException(
                $"A session record must contain 1-{MaximumServiceIds} service identifiers.",
                nameof(serviceIds));
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var serviceId in serviceIds)
        {
            if (string.IsNullOrEmpty(serviceId.Value) || !unique.Add(serviceId.Value))
            {
                throw new ArgumentException("Session service identifiers must be valid and unique.", nameof(serviceIds));
            }
        }
    }

    private static void ValidateProcesses(
        string recordBootId,
        IReadOnlyDictionary<string, ProcessIdentity> processes)
    {
        if (processes.Count > MaximumProcesses)
        {
            throw new ArgumentException(
                $"A session record cannot contain more than {MaximumProcesses} process identities.",
                nameof(processes));
        }

        var processIds = new HashSet<int>();
        foreach (var (name, identity) in processes)
        {
            if (!SessionProcessNames.All.Contains(name))
            {
                throw new ArgumentException($"Unknown session process name '{name}'.", nameof(processes));
            }

            if (identity is null)
            {
                throw new ArgumentException($"Process identity '{name}' cannot be null.", nameof(processes));
            }

            if (identity.ProcessId is < 1 or > MaximumLinuxProcessId)
            {
                throw new ArgumentException($"Process identity '{name}' has an invalid PID.", nameof(processes));
            }

            if (!processIds.Add(identity.ProcessId))
            {
                throw new ArgumentException("Session process identities must use distinct PIDs.", nameof(processes));
            }

            ValidateBootId(identity.BootId, nameof(processes));
            if (!string.Equals(recordBootId, identity.BootId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Process identity '{name}' was captured during a different boot.",
                    nameof(processes));
            }

            if (identity.StartTicks <= 0)
            {
                throw new ArgumentException($"Process identity '{name}' has invalid start ticks.", nameof(processes));
            }

            if (string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                identity.ExecutablePath.Length > MaximumExecutableCharacters ||
                !Path.IsPathFullyQualified(identity.ExecutablePath) ||
                identity.ExecutablePath.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Process identity '{name}' must contain a bounded absolute executable path.",
                    nameof(processes));
            }
        }
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
