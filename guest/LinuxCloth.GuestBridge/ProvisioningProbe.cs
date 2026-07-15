using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace LinuxCloth.GuestBridge;

internal enum ProvisioningProbeStatus
{
    NotFound,
    Invalid,
    Ambiguous,
    GuestBridgeHashMismatch,
    ResultWriteFailed,
    Success,
}

internal sealed record ProvisioningProbeOutcome(ProvisioningProbeStatus Status);

internal interface IProvisioningProbeProcessor
{
    Task<ProvisioningProbeOutcome> ProcessAsync(CancellationToken cancellationToken);
}

internal interface IGuestBridgeExecutableProvider
{
    string? GetExecutablePath();
}

internal sealed class SystemGuestBridgeExecutableProvider : IGuestBridgeExecutableProvider
{
    public string? GetExecutablePath() => Environment.ProcessPath;
}

internal sealed class NullProvisioningProbeProcessor : IProvisioningProbeProcessor
{
    public static NullProvisioningProbeProcessor Instance { get; } = new();

    private NullProvisioningProbeProcessor()
    {
    }

    public Task<ProvisioningProbeOutcome> ProcessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProvisioningProbeOutcome(ProvisioningProbeStatus.NotFound));
    }
}

internal sealed class ProvisioningProbeProcessor : IProvisioningProbeProcessor
{
    internal const string ProbeFileName = "linuxcloth-provision-probe.json";
    internal const string ResultFileName = "linuxcloth-provision-result.json";
    internal const int MaximumProbeBytes = 16 * 1024;
    internal const int SchemaVersion = 1;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly IConfigDriveProvider _driveProvider;
    private readonly IGuestBridgeExecutableProvider _executableProvider;
    private readonly IGuestEnvironmentProvider _environmentProvider;

    public ProvisioningProbeProcessor(
        IConfigDriveProvider driveProvider,
        IGuestBridgeExecutableProvider executableProvider,
        IGuestEnvironmentProvider environmentProvider)
    {
        _driveProvider = driveProvider ?? throw new ArgumentNullException(nameof(driveProvider));
        _executableProvider = executableProvider ?? throw new ArgumentNullException(nameof(executableProvider));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
    }

    public async Task<ProvisioningProbeOutcome> ProcessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = FindCandidates();
        if (candidates.Count == 0)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.NotFound);
        }

        if (candidates.Count != 1)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.Ambiguous);
        }

        var candidate = candidates[0];
        ProvisioningProbe probe;
        try
        {
            probe = ReadProbe(candidate.ProbePath);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.Invalid);
        }

        string executableHash;
        try
        {
            executableHash = await HashExecutableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException or
            InvalidDataException)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.Invalid);
        }

        if (!string.Equals(
                executableHash,
                probe.ExpectedGuestBridgeSha256,
                StringComparison.Ordinal))
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.GuestBridgeHashMismatch);
        }

        GuestEnvironmentProvenance provenance;
        try
        {
            provenance = _environmentProvider.GetProvenance();
            ValidateProvenance(provenance);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            PlatformNotSupportedException or
            SecurityException)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.Invalid);
        }

        try
        {
            WriteResultAtomically(
                candidate.DriveRoot,
                probe.Nonce,
                executableHash,
                provenance);
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.Success);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return new ProvisioningProbeOutcome(ProvisioningProbeStatus.ResultWriteFailed);
        }
    }

    private List<ProvisioningCandidate> FindCandidates()
    {
        var candidates = new List<ProvisioningCandidate>();
        var roots = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in _driveProvider.GetReadyDriveRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                var fullRoot = Path.GetFullPath(root);
                if (!roots.Add(fullRoot))
                {
                    continue;
                }

                var probePath = Path.Combine(fullRoot, ProbeFileName);
                if (File.Exists(probePath))
                {
                    candidates.Add(new ProvisioningCandidate(fullRoot, probePath));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                // A malformed drive root cannot contain a usable provisioning probe.
            }
        }

        return candidates;
    }

    private static ProvisioningProbe ReadProbe(string path)
    {
        EnsurePlainFile(path);
        var bytes = ReadBoundedFile(path, MaximumProbeBytes);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The provisioning probe root must be an object.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("The provisioning probe contains a duplicate property.");
            }
        }

        if (properties.Count != 3 ||
            !properties.TryGetValue("schemaVersion", out var schemaVersion) ||
            !properties.TryGetValue("nonce", out var nonceElement) ||
            !properties.TryGetValue("expectedGuestBridgeSha256", out var hashElement) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var schema) ||
            schema != SchemaVersion ||
            nonceElement.ValueKind != JsonValueKind.String ||
            hashElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The provisioning probe schema is invalid.");
        }

        var nonce = nonceElement.GetString()!;
        var expectedHash = hashElement.GetString()!;
        if (!IsCanonicalLowerHex(nonce, 32) || !IsCanonicalLowerHex(expectedHash, 64))
        {
            throw new InvalidDataException("The provisioning probe contains a non-canonical nonce or hash.");
        }

        return new ProvisioningProbe(nonce, expectedHash);
    }

    private async Task<string> HashExecutableAsync(CancellationToken cancellationToken)
    {
        var executablePath = _executableProvider.GetExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
        {
            throw new InvalidDataException("The running GuestBridge executable path is unavailable.");
        }

        var fullPath = Path.GetFullPath(executablePath);
        EnsurePlainFile(fullPath);
        var before = new FileInfo(fullPath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(fullPath);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new InvalidDataException("The running GuestBridge executable changed while hashing.");
        }

        return Convert.ToHexStringLower(hash);
    }

    private static void WriteResultAtomically(
        string driveRoot,
        string nonce,
        string guestBridgeHash,
        GuestEnvironmentProvenance provenance)
    {
        var resultPath = Path.Combine(driveRoot, ResultFileName);
        if (File.Exists(resultPath))
        {
            EnsurePlainFile(resultPath);
        }

        var temporaryPath = Path.Combine(
            driveRoot,
            $".{ResultFileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = false,
                    SkipValidation = false,
                });
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                writer.WriteString("nonce", nonce);
                writer.WriteString("guestBridgeSha256", guestBridgeHash);
                writer.WriteString("guestBridgeVersion", provenance.GuestBridgeVersion);
                writer.WriteString("windowsArchitecture", provenance.WindowsArchitecture);
                writer.WriteNumber("windowsBuild", provenance.WindowsBuild);
                writer.WriteString("windowsEditionId", provenance.WindowsEditionId);
                writer.WriteString("windowsDisplayVersion", provenance.WindowsDisplayVersion);
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, resultPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void EnsurePlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("The provisioning entry must be a regular file.");
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The provisioning probe has an invalid size.");
        }

        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("The provisioning probe changed while reading.");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The provisioning probe exceeds its size limit.");
        }

        return bytes;
    }

    private static bool IsCanonicalLowerHex(string value, int expectedLength) =>
        value.Length == expectedLength &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateProvenance(GuestEnvironmentProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (!IsBoundedControlFree(provenance.GuestBridgeVersion, 128) ||
            !string.Equals(provenance.WindowsArchitecture, "X64", StringComparison.Ordinal) ||
            provenance.WindowsBuild < 22000 ||
            !IsBoundedControlFree(provenance.WindowsEditionId, 128) ||
            !IsBoundedControlFree(provenance.WindowsDisplayVersion, 64))
        {
            throw new InvalidDataException("The Windows guest provenance is invalid.");
        }
    }

    private static bool IsBoundedControlFree(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private sealed record ProvisioningCandidate(string DriveRoot, string ProbePath);

    private sealed record ProvisioningProbe(string Nonce, string ExpectedGuestBridgeSha256);
}
