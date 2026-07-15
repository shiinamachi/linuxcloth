using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxCloth.Application.Storage;

namespace LinuxCloth.Desktop.Setup;

public enum SetupStep
{
    HostInspection,
    Components,
    WindowsMedia,
    VirtioMedia,
    ImageBuild,
}

public sealed record SetupState(
    int SchemaVersion,
    int WizardVersion,
    SetupStep LastStep,
    string NoticeVersion,
    bool RememberMediaPaths,
    string? WindowsIsoPath,
    string? VirtioIsoPath,
    string? StagingDirectory)
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentWizardVersion = 1;
    public const string CurrentNoticeVersion = "technical-preview-1";

    public static SetupState Default { get; } = new(
        CurrentSchemaVersion,
        CurrentWizardVersion,
        SetupStep.HostInspection,
        CurrentNoticeVersion,
        RememberMediaPaths: false,
        WindowsIsoPath: null,
        VirtioIsoPath: null,
        StagingDirectory: null);
}

public interface ISetupStateStore
{
    Task<SetupState> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SetupState state, CancellationToken cancellationToken = default);
}

public sealed partial class SetupStateStore : ISetupStateStore
{
    public const string FileName = "setup-state.json";
    public const int MaximumFileBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly string _configDirectory;
    private readonly string _path;

    public SetupStateStore(LinuxClothPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDirectory)
    {
    }

    public SetupStateStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        if (!Path.IsPathFullyQualified(configDirectory))
        {
            throw new ArgumentException("설정 디렉터리는 절대 경로여야 합니다.", nameof(configDirectory));
        }

        _configDirectory = Path.GetFullPath(configDirectory);
        _path = Path.Combine(_configDirectory, FileName);
    }

    public async Task<SetupState> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsurePrivateDirectory();
        if (!File.Exists(_path))
        {
            return SetupState.Default;
        }

        RejectReparsePoint(_path, "초기 설정 상태 파일");
        var before = new FileInfo(_path);
        if (before.Length <= 0 || before.Length > MaximumFileBytes)
        {
            return SetupState.Default;
        }

        var contents = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(_path);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new IOException("초기 설정 상태 파일이 읽는 동안 변경되었습니다.");
        }

        try
        {
            using var document = JsonDocument.Parse(contents, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            var state = JsonSerializer.Deserialize<SetupState>(contents, SerializerOptions);
            return state is null ? SetupState.Default : Normalize(state);
        }
        catch (JsonException)
        {
            return SetupState.Default;
        }
        catch (InvalidDataException)
        {
            return SetupState.Default;
        }
    }

    public async Task SaveAsync(
        SetupState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsurePrivateDirectory();
        var normalized = Normalize(state);
        var contents = JsonSerializer.SerializeToUtf8Bytes(normalized, SerializerOptions);
        if (contents.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("초기 설정 상태가 허용 크기를 초과했습니다.");
        }

        var temporaryPath = Path.Combine(
            _configDirectory,
            $".{FileName}.tmp-{Guid.NewGuid():N}");
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

    private static SetupState Normalize(SetupState state)
    {
        if (state.SchemaVersion != SetupState.CurrentSchemaVersion ||
            state.WizardVersion <= 0 ||
            !Enum.IsDefined(state.LastStep) ||
            string.IsNullOrWhiteSpace(state.NoticeVersion) ||
            state.NoticeVersion.Length > 128 ||
            state.NoticeVersion.Any(char.IsControl))
        {
            throw new InvalidDataException("초기 설정 상태 스키마 또는 값이 올바르지 않습니다.");
        }

        var windowsIso = state.RememberMediaPaths
            ? NormalizeOptionalAbsolutePath(state.WindowsIsoPath, "Windows ISO")
            : null;
        var virtioIso = state.RememberMediaPaths
            ? NormalizeOptionalAbsolutePath(state.VirtioIsoPath, "virtio ISO")
            : null;
        var staging = NormalizeOptionalAbsolutePath(state.StagingDirectory, "스테이징 디렉터리");
        return state with
        {
            WindowsIsoPath = windowsIso,
            VirtioIsoPath = virtioIso,
            StagingDirectory = staging,
        };
    }

    private static string? NormalizeOptionalAbsolutePath(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"{description} 경로가 올바르지 않습니다.");
        }

        return Path.GetFullPath(path);
    }

    private void EnsurePrivateDirectory()
    {
        if (File.Exists(_configDirectory))
        {
            throw new IOException("초기 설정 경로에 디렉터리 대신 파일이 있습니다.");
        }

        if (Directory.Exists(_configDirectory))
        {
            RejectReparsePoint(_configDirectory, "초기 설정 디렉터리");
        }

        Directory.CreateDirectory(_configDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                _configDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void RejectExistingTargetReparsePoint()
    {
        if (File.Exists(_path) || Directory.Exists(_path))
        {
            RejectReparsePoint(_path, "기존 초기 설정 상태 경로");
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"{description}에 심볼릭 링크를 사용할 수 없습니다.");
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
                        throw new JsonException($"중복 JSON 속성: {property.Name}");
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
        var descriptor = NativeMethods.Open(_configDirectory, readOnly | directory);
        if (descriptor < 0)
        {
            throw new IOException(
                $"초기 설정 디렉터리를 동기화할 수 없습니다. errno={Marshal.GetLastPInvokeError()}");
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"초기 설정 디렉터리를 동기화할 수 없습니다. errno={Marshal.GetLastPInvokeError()}");
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
}
