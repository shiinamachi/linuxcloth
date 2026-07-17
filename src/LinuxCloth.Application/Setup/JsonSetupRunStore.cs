using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxCloth.Application.Images;

namespace LinuxCloth.Application.Setup;

public sealed partial class JsonSetupRunStore : ISetupRunStore
{
    public const string FileName = "setup-run.json";
    public const int MaximumFileBytes = 128 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new ImageIdJsonConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 24,
    };

    private readonly string _directory;
    private readonly string _path;

    public JsonSetupRunStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        if (!Path.IsPathFullyQualified(configDirectory))
        {
            throw new ArgumentException("The setup run directory must be an absolute path.", nameof(configDirectory));
        }

        _directory = Path.GetFullPath(configDirectory);
        _path = Path.Combine(_directory, FileName);
    }

    public async Task<SetupRun?> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsurePrivateDirectory();
        if (!File.Exists(_path))
        {
            return null;
        }

        RejectReparsePoint(_path, "setup run file");
        var before = new FileInfo(_path);
        if (before.Length <= 0 || before.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The setup run file has an invalid size.");
        }

        var contents = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(_path);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new IOException("The setup run file changed while it was being read.");
        }

        try
        {
            using var document = JsonDocument.Parse(contents, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            var run = JsonSerializer.Deserialize<SetupRun>(contents, SerializerOptions)
                ?? throw new InvalidDataException("The setup run file is empty.");
            return Normalize(run);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The setup run file is not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(SetupRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        EnsurePrivateDirectory();
        var contents = JsonSerializer.SerializeToUtf8Bytes(Normalize(run), SerializerOptions);
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The setup run exceeds the maximum allowed size.");
        }

        var temporaryPath = Path.Combine(_directory, $".{FileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                SetPrivateFileMode(temporaryPath);
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            RejectExistingTargetReparsePoint();
            File.Move(temporaryPath, _path, overwrite: true);
            SetPrivateFileMode(_path);
            SynchronizeDirectory();
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePrivateDirectory();
        RejectExistingTargetReparsePoint();
        File.Delete(_path);
        SynchronizeDirectory();
        return Task.CompletedTask;
    }

    private static SetupRun Normalize(SetupRun run)
    {
        if (run.SchemaVersion != SetupRun.CurrentSchemaVersion ||
            run.RunId == Guid.Empty ||
            !Enum.IsDefined(run.Phase) ||
            run.Attempt < 0 ||
            run.StartedAt == default ||
            run.UpdatedAt < run.StartedAt ||
            run.Inputs.DiskSizeGiB is < 64 or > 1024 ||
            run.Inputs.CpuCount is < 2 or > 32 ||
            run.Inputs.MemoryMiB is < 4096 or > 131072)
        {
            throw new InvalidDataException("The setup run contains unsupported values.");
        }

        ImageId.ValidateInitialized(run.Inputs.ImageId);

        if (run.Phase == SetupPhase.Blocked != (run.Blocker is not null))
        {
            throw new InvalidDataException("The setup run blocker does not match its phase.");
        }

        ValidateText(run.Inputs.WindowsEdition, 256, "Windows edition");
        ValidateText(run.Inputs.PackagePlanDigest, 128, "package plan digest");
        ValidateFingerprint(run.Inputs.WindowsIsoFingerprint, "Windows ISO fingerprint");
        ValidateFingerprint(run.Inputs.VirtioIsoFingerprint, "virtio ISO fingerprint");
        ValidateBlocker(run.Blocker);

        return run with
        {
            Inputs = run.Inputs with
            {
                WindowsIsoPath = NormalizeOptionalAbsolutePath(run.Inputs.WindowsIsoPath, "Windows ISO"),
                VirtioIsoPath = NormalizeOptionalAbsolutePath(run.Inputs.VirtioIsoPath, "virtio ISO"),
            },
            ImageBuildStagingDirectory = NormalizeOptionalAbsolutePath(
                run.ImageBuildStagingDirectory,
                "image build staging directory"),
        };
    }

    private static void ValidateFingerprint(SetupFileFingerprint? fingerprint, string description)
    {
        if (fingerprint is null)
        {
            return;
        }

        if (fingerprint.Length <= 0 ||
            fingerprint.LastWriteTimeUtc == default ||
            fingerprint.Sha256.Length != 64 ||
            fingerprint.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"The {description} is invalid.");
        }
    }

    private static void ValidateBlocker(SetupBlocker? blocker)
    {
        if (blocker is null)
        {
            return;
        }

        if (blocker.Phase is SetupPhase.Blocked or SetupPhase.Completed ||
            !Enum.IsDefined(blocker.Kind))
        {
            throw new InvalidDataException("The setup blocker contains unsupported values.");
        }

        ValidateRequiredText(blocker.Code, 128, "blocker code");
        ValidateRequiredText(blocker.Title, 256, "blocker title");
        ValidateRequiredText(blocker.Description, 2048, "blocker description");
        ValidateRequiredText(blocker.ActionLabel, 128, "blocker action label");
        ValidateText(blocker.TechnicalDetail, 8192, "blocker technical detail");
    }

    private static void ValidateRequiredText(string text, int maximumLength, string description)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"The {description} is required.");
        }

        ValidateText(text, maximumLength, description);
    }

    private static void ValidateText(string? text, int maximumLength, string description)
    {
        if (text is not null && (text.Length > maximumLength || text.Any(char.IsControl)))
        {
            throw new InvalidDataException($"The {description} is invalid.");
        }
    }

    private static string? NormalizeOptionalAbsolutePath(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"The {description} path is invalid.");
        }

        return Path.GetFullPath(path);
    }

    private void EnsurePrivateDirectory()
    {
        if (File.Exists(_directory))
        {
            throw new IOException("A file exists where the setup run directory is required.");
        }

        if (Directory.Exists(_directory))
        {
            RejectReparsePoint(_directory, "setup run directory");
        }

        Directory.CreateDirectory(_directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                _directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void RejectExistingTargetReparsePoint()
    {
        if (File.Exists(_path) || Directory.Exists(_path))
        {
            RejectReparsePoint(_path, "existing setup run path");
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The {description} cannot be a symbolic link.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new JsonException($"Duplicate JSON property: {property.Name}");
                    }

                    RejectDuplicateProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectDuplicateProperties(item);
                }

                break;
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private void SynchronizeDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int readOnly = 0;
        const int directory = 0x10000;
        var descriptor = NativeMethods.Open(_directory, readOnly | directory);
        if (descriptor < 0)
        {
            throw new IOException($"The setup run directory could not be synchronized. errno={Marshal.GetLastPInvokeError()}");
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new IOException($"The setup run directory could not be synchronized. errno={Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            _ = NativeMethods.Close(descriptor);
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static partial int Fsync(int fileDescriptor);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fileDescriptor);
    }

    private sealed class ImageIdJsonConverter : JsonConverter<ImageId>
    {
        public override ImageId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            _ = typeToConvert;
            _ = options;
            return ImageId.Parse(
                reader.GetString()
                ?? throw new JsonException("The image identifier must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            ImageId value,
            JsonSerializerOptions options)
        {
            _ = options;
            writer.WriteStringValue(value.Value);
        }
    }
}
