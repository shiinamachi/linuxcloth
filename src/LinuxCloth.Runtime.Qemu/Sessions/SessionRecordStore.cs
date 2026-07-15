using System.Runtime.InteropServices;
using System.Text.Json;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed partial class SessionRecordStore
{
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly string[] RootPropertyNames =
    [
        "schemaVersion",
        "sessionId",
        "bootId",
        "state",
        "imageId",
        "baseSha256",
        "serviceIds",
        "processes",
    ];
    private static readonly string[] ProcessPropertyNames = ["pid", "bootId", "startTicks", "executable"];
    private readonly int _maximumRecordBytes = SessionRecordValidation.MaximumRecordBytes;

    public Task WriteAsync(
        SessionPaths paths,
        SessionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(record);
        if (paths.SessionId != record.SessionId)
        {
            throw new ArgumentException("The record session identifier does not match its session path.", nameof(record));
        }

        return WriteAsync(paths.SessionRecordPath, record, cancellationToken);
    }

    public async Task WriteAsync(
        string path,
        SessionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(record);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The session record path must be absolute.", nameof(path));
        }

        SessionRecordValidation.Validate(record);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = Serialize(record);
        if (payload.Length > _maximumRecordBytes)
        {
            throw new InvalidDataException(
                $"The session record exceeds {_maximumRecordBytes} bytes.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The session record path has no parent directory.", nameof(path));
        EnsurePrivateDirectory(directory);
        RefuseSymbolicLink(fullPath);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, CreateWriteOptions()))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(temporaryPath, PrivateFileMode);
                }

                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(fullPath, PrivateFileMode);
                SynchronizeDirectory(directory);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed write must not hide its original error. Recovery ignores temporary files.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed write must not hide its original error. Recovery ignores temporary files.
            }
        }
    }

    public Task<SessionRecord> ReadAsync(
        SessionPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ReadForPathAsync(paths, cancellationToken);
    }

    public async Task<SessionRecord> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The session record path must be absolute.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        try
        {
            RefuseSymbolicLink(fullPath);
            EnsurePrivateMode(fullPath);
            var payload = await ReadBoundedAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return Deserialize(payload);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The session record is invalid.", exception);
        }
    }

    private async Task<SessionRecord> ReadForPathAsync(
        SessionPaths paths,
        CancellationToken cancellationToken)
    {
        var record = await ReadAsync(paths.SessionRecordPath, cancellationToken).ConfigureAwait(false);
        if (record.SessionId != paths.SessionId)
        {
            throw new InvalidDataException("The session record identifier does not match its directory name.");
        }

        return record;
    }

    private static byte[] Serialize(SessionRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", record.SchemaVersion);
            writer.WriteString("sessionId", record.SessionId.ToString("D"));
            writer.WriteString("bootId", record.BootId);
            writer.WriteString("state", record.State.ToString());
            writer.WriteString("imageId", record.ImageId);
            writer.WriteString("baseSha256", record.BaseSha256);
            writer.WriteStartArray("serviceIds");
            foreach (var serviceId in record.ServiceIds)
            {
                writer.WriteStringValue(serviceId.Value);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("processes");
            foreach (var (name, identity) in record.Processes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                writer.WriteNumber("pid", identity.ProcessId);
                writer.WriteString("bootId", identity.BootId);
                writer.WriteNumber("startTicks", identity.StartTicks);
                writer.WriteString("executable", identity.ExecutablePath);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static SessionRecord Deserialize(ReadOnlyMemory<byte> payload)
    {
        using var document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        var root = ReadStrictObject(document.RootElement, RootPropertyNames, "session record");

        var schemaVersion = ReadInt32(root["schemaVersion"], "schemaVersion");
        var sessionIdText = ReadString(root["sessionId"], "sessionId");
        if (!Guid.TryParseExact(sessionIdText, "D", out var sessionId) ||
            !string.Equals(sessionIdText, sessionId.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("The sessionId must be a canonical lowercase UUID.");
        }

        var bootId = ReadString(root["bootId"], "bootId");
        var stateText = ReadString(root["state"], "state");
        if (!Enum.TryParse<SessionState>(stateText, ignoreCase: false, out var state) || !Enum.IsDefined(state))
        {
            throw new InvalidDataException($"Unknown session state '{stateText}'.");
        }

        var serviceIdsElement = root["serviceIds"];
        if (serviceIdsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("serviceIds must be an array.");
        }

        var serviceIds = new List<ServiceId>();
        foreach (var element in serviceIdsElement.EnumerateArray())
        {
            if (serviceIds.Count >= SessionRecordValidation.MaximumServiceIds)
            {
                throw new InvalidDataException("The session record contains too many service identifiers.");
            }

            serviceIds.Add(ServiceId.Parse(ReadString(element, "serviceIds[]")));
        }

        var processesElement = root["processes"];
        if (processesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("processes must be an object.");
        }

        var processes = new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal);
        foreach (var processProperty in processesElement.EnumerateObject())
        {
            if (!SessionProcessNames.All.Contains(processProperty.Name))
            {
                throw new InvalidDataException($"Unknown session process property '{processProperty.Name}'.");
            }

            if (!processes.TryAdd(processProperty.Name, ReadProcessIdentity(processProperty.Value)))
            {
                throw new InvalidDataException($"Duplicate session process property '{processProperty.Name}'.");
            }
        }

        try
        {
            return new SessionRecord(
                sessionId,
                bootId,
                state,
                ReadString(root["imageId"], "imageId"),
                ReadString(root["baseSha256"], "baseSha256"),
                serviceIds,
                processes,
                schemaVersion);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("The session record violates its schema constraints.", exception);
        }
    }

    private static ProcessIdentity ReadProcessIdentity(JsonElement element)
    {
        var properties = ReadStrictObject(element, ProcessPropertyNames, "process identity");
        return new ProcessIdentity(
            ReadInt32(properties["pid"], "pid"),
            ReadString(properties["bootId"], "bootId"),
            ReadInt64(properties["startTicks"], "startTicks"),
            ReadString(properties["executable"], "executable"));
    }

    private static Dictionary<string, JsonElement> ReadStrictObject(
        JsonElement element,
        IReadOnlyCollection<string> allowedProperties,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The {description} must be an object.");
        }

        var expected = new HashSet<string>(allowedProperties, StringComparer.Ordinal);
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                throw new InvalidDataException($"Unknown {description} property '{property.Name}'.");
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException($"Duplicate {description} property '{property.Name}'.");
            }
        }

        var missing = expected.Where(name => !properties.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"The {description} is missing: {string.Join(", ", missing)}.");
        }

        return properties;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{propertyName} must be a string.");
        }

        return element.GetString() ?? throw new InvalidDataException($"{propertyName} cannot be null.");
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new InvalidDataException($"{propertyName} must be a 32-bit integer.");
        }

        return value;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw new InvalidDataException($"{propertyName} must be a 64-bit integer.");
        }

        return value;
    }

    private async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 4096,
        };
        await using var stream = new FileStream(path, options);
        if (stream.Length <= 0 || stream.Length > _maximumRecordBytes)
        {
            throw new InvalidDataException(
                $"The session record must be 1-{_maximumRecordBytes} bytes.");
        }

        var buffer = new byte[_maximumRecordBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        if (total == 0 || total > _maximumRecordBytes)
        {
            throw new InvalidDataException(
                $"The session record must be 1-{_maximumRecordBytes} bytes.");
        }

        return buffer.AsMemory(0, total);
    }

    private static FileStreamOptions CreateWriteOptions()
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            BufferSize = 4096,
        };
        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        return options;
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        var information = new DirectoryInfo(directory);
        if (!information.Exists)
        {
            throw new DirectoryNotFoundException($"Session record directory '{directory}' does not exist.");
        }

        if (information.LinkTarget is not null)
        {
            throw new InvalidOperationException("Refusing to write a session record through a symbolic-link directory.");
        }
    }

    private static void RefuseSymbolicLink(string path)
    {
        var information = new FileInfo(path);
        if (information.Exists && information.LinkTarget is not null)
        {
            throw new InvalidDataException("Refusing to use a symbolic-link session record.");
        }
    }

    private static void EnsurePrivateMode(string path)
    {
        if (OperatingSystem.IsLinux() && File.GetUnixFileMode(path) != PrivateFileMode)
        {
            throw new InvalidDataException("The session record must have mode 0600.");
        }
    }

    private static void SynchronizeDirectory(string directory)
    {
        var descriptor = NativeMethods.Open(
            directory,
            NativeMethods.OpenReadOnly | NativeMethods.OpenDirectory | NativeMethods.OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new IOException($"Could not open the session directory for synchronization; errno={Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new IOException($"Could not synchronize the session directory; errno={Marshal.GetLastPInvokeError()}.");
            }
        }
        finally
        {
            _ = NativeMethods.Close(descriptor);
        }
    }

    private static partial class NativeMethods
    {
        public const int OpenReadOnly = 0;
        public const int OpenDirectory = 0x10000;
        public const int OpenCloseOnExec = 0x80000;

        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static partial int Fsync(int descriptor);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        public static partial int Close(int descriptor);
    }
}
