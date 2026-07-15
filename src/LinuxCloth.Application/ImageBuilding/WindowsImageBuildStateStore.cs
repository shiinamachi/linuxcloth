using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public static partial class WindowsImageBuildStateStore
{
    public const string ManifestFileName = ".linuxcloth-image-build.json";
    public const int MaximumManifestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    public static async Task WriteAsync(
        ImageRegistrationStaging staging,
        WindowsImageBuildState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, staging.ImageId);

        var contents = JsonSerializer.SerializeToUtf8Bytes(ToDto(state), SerializerOptions);
        if (contents.Length > MaximumManifestBytes)
        {
            throw new WindowsImageBuildException("The image-build state manifest exceeds its size limit.", staging);
        }

        var targetPath = GetManifestPath(staging);
        var temporaryPath = Path.Combine(
            staging.DirectoryPath,
            $"{ManifestFileName}.tmp-{Guid.NewGuid():N}");
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

            File.Move(temporaryPath, targetPath, overwrite: true);
            SetPrivateFileMode(targetPath);
            SynchronizeDirectory(staging.DirectoryPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static async Task<WindowsImageBuildState> ReadAsync(
        ImageRegistrationStaging staging,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        var path = GetManifestPath(staging);
        ImageBuildPathGuard.RequireRegularFile(path, "image-build state manifest");

        var before = new FileInfo(path);
        if (before.Length <= 0 || before.Length > MaximumManifestBytes)
        {
            throw new WindowsImageBuildException("The image-build state manifest has an invalid size.", staging);
        }

        var contents = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var after = new FileInfo(path);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc)
        {
            throw new WindowsImageBuildException(
                "The image-build state manifest changed while it was being read.",
                staging);
        }

        try
        {
            using var document = JsonDocument.Parse(contents, DocumentOptions);
            RejectDuplicateProperties(document.RootElement);
            var dto = JsonSerializer.Deserialize<ImageBuildManifestDto>(contents, SerializerOptions) ??
                      throw new WindowsImageBuildException("The image-build state manifest is empty.", staging);
            var state = FromDto(dto);
            ValidateState(state, staging.ImageId);
            return state;
        }
        catch (JsonException exception)
        {
            throw new WindowsImageBuildException(
                "The image-build state manifest is not valid strict JSON.",
                exception,
                staging);
        }
    }

    public static string GetManifestPath(ImageRegistrationStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        return Path.Combine(staging.DirectoryPath, ManifestFileName);
    }

    public static void DeleteBuilderArtifacts(ImageRegistrationStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);
        File.Delete(GetManifestPath(staging));

        foreach (var entry in Directory.EnumerateFiles(
                     staging.DirectoryPath,
                     $"{ManifestFileName}.tmp-*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(entry);
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

    private static void ValidateState(WindowsImageBuildState state, ImageId expectedImageId)
    {
        if (state.SchemaVersion != WindowsImageBuildState.CurrentSchemaVersion)
        {
            throw new WindowsImageBuildException("The image-build state schema version is unsupported.");
        }

        _ = state.ImageId.Value;
        if (state.ImageId != expectedImageId)
        {
            throw new WindowsImageBuildException("The image-build state does not match its staging image ID.");
        }

        if (state.MachineId == Guid.Empty || !Enum.IsDefined(state.Phase))
        {
            throw new WindowsImageBuildException("The image-build state has an invalid machine or phase value.");
        }

        ValidateResources(state.DiskSizeGiB, state.CpuCount, state.MemoryMiB);
        ValidateFingerprint(state.WindowsIso, "Windows ISO");
        ValidateFingerprint(state.VirtioWinIso, "virtio-win ISO");
        ValidateFingerprint(state.GuestBridgeExecutable, "GuestBridge executable");
        ValidateFingerprint(state.OvmfCode, "OVMF code");
        ValidateFingerprint(state.OvmfVariablesSource, "OVMF variables source");
        ValidateToolchain(state.Toolchain);
        ValidateActiveProcessState(state);
        ValidateVerifiedGuestEnvironment(state);

        if (state.CreatedAt == default || state.UpdatedAt < state.CreatedAt)
        {
            throw new WindowsImageBuildException("The image-build state timestamps are invalid.");
        }
    }

    public static void ValidateResources(int diskSizeGiB, int cpuCount, int memoryMiB)
    {
        if (diskSizeGiB is < 64 or > 1024)
        {
            throw new WindowsImageBuildException("The Windows disk size must be between 64 and 1024 GiB.");
        }

        if (cpuCount is < 2 or > 32)
        {
            throw new WindowsImageBuildException("The installer CPU count must be between 2 and 32.");
        }

        if (memoryMiB is < 4096 or > 131072)
        {
            throw new WindowsImageBuildException("The installer memory must be between 4096 and 131072 MiB.");
        }
    }

    private static void ValidateActiveProcessState(WindowsImageBuildState state)
    {
        ArgumentNullException.ThrowIfNull(state.ActiveProcesses);
        var isRunning = state.Phase is WindowsImageBuildPhase.InstallerRunning or
            WindowsImageBuildPhase.VerificationRunning;
        if (!isRunning)
        {
            if (state.ActiveHostBootId is not null ||
                state.PendingProcessName is not null ||
                state.ActiveProcesses.Count != 0 ||
                state.VerificationNonce is not null)
            {
                throw new WindowsImageBuildException(
                    "A non-running image build cannot retain active process recovery state.");
            }

            return;
        }

        ValidateBootId(state.ActiveHostBootId, "active host boot ID");
        if (state.PendingProcessName is not null &&
            !WindowsImageBuildProcessNames.All.Contains(state.PendingProcessName))
        {
            throw new WindowsImageBuildException("The pending image-build process name is invalid.");
        }

        if (state.ActiveProcesses.Count > WindowsImageBuildProcessNames.All.Count)
        {
            throw new WindowsImageBuildException("The image-build process identity set is too large.");
        }

        foreach (var (name, identity) in state.ActiveProcesses)
        {
            if (!WindowsImageBuildProcessNames.All.Contains(name) || identity is null)
            {
                throw new WindowsImageBuildException("The image-build process identity name is invalid.");
            }

            if (identity.ProcessId <= 0 || identity.StartTicks <= 0)
            {
                throw new WindowsImageBuildException("An image-build process identity is invalid.");
            }

            ValidateBootId(identity.BootId, "process boot ID");
            if (!string.Equals(identity.BootId, state.ActiveHostBootId, StringComparison.Ordinal))
            {
                throw new WindowsImageBuildException(
                    "An image-build process belongs to a different host boot than the active run.");
            }
            var executable = ImageBuildPathGuard.NormalizeAbsolute(
                identity.ExecutablePath,
                "process executable");
            if (!string.Equals(executable, identity.ExecutablePath, StringComparison.Ordinal))
            {
                throw new WindowsImageBuildException(
                    "An image-build process executable path is not normalized.");
            }

            var expectedExecutable = name switch
            {
                WindowsImageBuildProcessNames.Swtpm => state.Toolchain.Swtpm,
                WindowsImageBuildProcessNames.Qemu => state.Toolchain.QemuSystem,
                WindowsImageBuildProcessNames.Viewer => state.Toolchain.RemoteViewer,
                _ => throw new WindowsImageBuildException("The image-build process name is invalid."),
            };
            if (!string.Equals(identity.ExecutablePath, expectedExecutable, StringComparison.Ordinal))
            {
                throw new WindowsImageBuildException(
                    $"The persisted {name} identity does not match the pinned executable.");
            }
        }

        if (state.PendingProcessName is not null &&
            state.ActiveProcesses.ContainsKey(state.PendingProcessName))
        {
            throw new WindowsImageBuildException(
                "A process cannot be both pending and durably identified.");
        }

        if (state.Phase == WindowsImageBuildPhase.VerificationRunning)
        {
            if (!IsLowercaseHex(state.VerificationNonce, 32))
            {
                throw new WindowsImageBuildException("The verification nonce is invalid.");
            }
        }
        else if (state.VerificationNonce is not null)
        {
            throw new WindowsImageBuildException(
                "Only a running verification may retain a verification nonce.");
        }
    }

    private static void ValidateVerifiedGuestEnvironment(WindowsImageBuildState state)
    {
        var environment = state.VerifiedGuestEnvironment;
        if (state.Phase != WindowsImageBuildPhase.ReadyToFinalize)
        {
            if (environment is not null)
            {
                throw new WindowsImageBuildException(
                    "Only a successfully verified image may retain guest environment provenance.");
            }

            return;
        }

        if (environment is null ||
            !string.Equals(environment.WindowsArchitecture, "X64", StringComparison.Ordinal) ||
            environment.WindowsBuild < 22000 ||
            !IsBoundedText(environment.GuestBridgeVersion, 128) ||
            !IsBoundedText(environment.WindowsEditionId, 128) ||
            !IsBoundedText(environment.WindowsDisplayVersion, 64) ||
            environment.VerifiedAt == default ||
            environment.VerifiedAt.Offset != TimeSpan.Zero)
        {
            throw new WindowsImageBuildException(
                "The verified GuestBridge and Windows environment provenance is invalid.");
        }
    }

    private static void ValidateBootId(string? value, string description)
    {
        if (value is null ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new WindowsImageBuildException($"The {description} is invalid.");
        }
    }

    private static void ValidateFingerprint(ImageBuildFileFingerprint fingerprint, string description)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var normalized = ImageBuildPathGuard.NormalizeAbsolute(fingerprint.Path, description);
        if (!string.Equals(normalized, fingerprint.Path, StringComparison.Ordinal) ||
            fingerprint.Length <= 0 ||
            fingerprint.LastWriteUtcTicks <= 0 ||
            !IsLowercaseSha256(fingerprint.Sha256))
        {
            throw new WindowsImageBuildException($"The {description} fingerprint is invalid.");
        }
    }

    private static void ValidateToolchain(WindowsImageBuildToolchain toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        foreach (var (path, description) in new[]
                 {
                     (toolchain.QemuSystem, "QEMU executable"),
                     (toolchain.QemuImg, "qemu-img executable"),
                     (toolchain.Swtpm, "swtpm executable"),
                     (toolchain.RemoteViewer, "remote-viewer executable"),
                     (toolchain.Xorriso, "xorriso executable"),
                     (toolchain.Bubblewrap, "Bubblewrap executable"),
                 })
        {
            var normalized = ImageBuildPathGuard.NormalizeAbsolute(path, description);
            if (!string.Equals(normalized, path, StringComparison.Ordinal))
            {
                throw new WindowsImageBuildException($"The {description} path is not normalized.");
            }
        }
    }

    private static bool IsLowercaseSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        return value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsLowercaseHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsBoundedText(string? value, int maximumLength) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static ImageBuildManifestDto ToDto(WindowsImageBuildState state) =>
        new()
        {
            SchemaVersion = state.SchemaVersion,
            ImageId = state.ImageId.Value,
            MachineId = state.MachineId,
            Phase = state.Phase.ToString(),
            WindowsIso = ToDto(state.WindowsIso),
            VirtioWinIso = ToDto(state.VirtioWinIso),
            GuestBridgeExecutable = ToDto(state.GuestBridgeExecutable),
            OvmfCode = ToDto(state.OvmfCode),
            OvmfVariablesSource = ToDto(state.OvmfVariablesSource),
            Toolchain = new ToolchainDto
            {
                QemuSystem = state.Toolchain.QemuSystem,
                QemuImg = state.Toolchain.QemuImg,
                Swtpm = state.Toolchain.Swtpm,
                RemoteViewer = state.Toolchain.RemoteViewer,
                Xorriso = state.Toolchain.Xorriso,
                Bubblewrap = state.Toolchain.Bubblewrap,
            },
            DiskSizeGiB = state.DiskSizeGiB,
            CpuCount = state.CpuCount,
            MemoryMiB = state.MemoryMiB,
            ActiveHostBootId = state.ActiveHostBootId,
            PendingProcessName = state.PendingProcessName,
            ActiveProcesses = state.ActiveProcesses.ToDictionary(
                static pair => pair.Key,
                static pair => ToDto(pair.Value),
                StringComparer.Ordinal),
            VerificationNonce = state.VerificationNonce,
            VerifiedGuestEnvironment = state.VerifiedGuestEnvironment is null
                ? null
                : ToDto(state.VerifiedGuestEnvironment),
            CreatedAt = state.CreatedAt,
            UpdatedAt = state.UpdatedAt,
        };

    private static FileFingerprintDto ToDto(ImageBuildFileFingerprint fingerprint) =>
        new()
        {
            Path = fingerprint.Path,
            Sha256 = fingerprint.Sha256,
            Length = fingerprint.Length,
            LastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
        };

    private static WindowsImageBuildState FromDto(ImageBuildManifestDto dto)
    {
        if (!Enum.TryParse<WindowsImageBuildPhase>(dto.Phase, ignoreCase: false, out var phase) ||
            !Enum.IsDefined(phase))
        {
            throw new WindowsImageBuildException("The image-build state phase is invalid.");
        }

        return new WindowsImageBuildState(
            dto.SchemaVersion,
            ImageId.Parse(dto.ImageId),
            dto.MachineId,
            phase,
            FromDto(dto.WindowsIso),
            FromDto(dto.VirtioWinIso),
            FromDto(dto.GuestBridgeExecutable),
            FromDto(dto.OvmfCode),
            FromDto(dto.OvmfVariablesSource),
            new WindowsImageBuildToolchain(
                dto.Toolchain.QemuSystem,
                dto.Toolchain.QemuImg,
                dto.Toolchain.Swtpm,
                dto.Toolchain.RemoteViewer,
                dto.Toolchain.Xorriso,
                dto.Toolchain.Bubblewrap),
            dto.DiskSizeGiB,
            dto.CpuCount,
            dto.MemoryMiB,
            dto.ActiveHostBootId,
            dto.PendingProcessName,
            dto.ActiveProcesses.ToDictionary(
                static pair => pair.Key,
                static pair => FromDto(pair.Value),
                StringComparer.Ordinal),
            dto.VerificationNonce,
            dto.VerifiedGuestEnvironment is null
                ? null
                : FromDto(dto.VerifiedGuestEnvironment),
            dto.CreatedAt,
            dto.UpdatedAt);
    }

    private static ImageBuildFileFingerprint FromDto(FileFingerprintDto dto) =>
        new(dto.Path, dto.Sha256, dto.Length, dto.LastWriteUtcTicks);

    private static ProcessIdentityDto ToDto(ProcessIdentity identity) =>
        new()
        {
            ProcessId = identity.ProcessId,
            BootId = identity.BootId,
            StartTicks = identity.StartTicks,
            ExecutablePath = identity.ExecutablePath,
        };

    private static ProcessIdentity FromDto(ProcessIdentityDto dto) =>
        new(dto.ProcessId, dto.BootId, dto.StartTicks, dto.ExecutablePath);

    private static VerifiedGuestEnvironmentDto ToDto(VerifiedGuestEnvironment environment) =>
        new()
        {
            GuestBridgeVersion = environment.GuestBridgeVersion,
            WindowsArchitecture = environment.WindowsArchitecture,
            WindowsBuild = environment.WindowsBuild,
            WindowsEditionId = environment.WindowsEditionId,
            WindowsDisplayVersion = environment.WindowsDisplayVersion,
            VerifiedAt = environment.VerifiedAt,
        };

    private static VerifiedGuestEnvironment FromDto(VerifiedGuestEnvironmentDto dto) =>
        new(
            dto.GuestBridgeVersion,
            dto.WindowsArchitecture,
            dto.WindowsBuild,
            dto.WindowsEditionId,
            dto.WindowsDisplayVersion,
            dto.VerifiedAt);

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void SynchronizeDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = NativeMethods.Open(
            directory,
            NativeMethods.OpenReadOnly | NativeMethods.OpenDirectory | NativeMethods.OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open the image-build staging directory for synchronization; errno={Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            if (NativeMethods.Fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"Could not synchronize the image-build staging directory; errno={Marshal.GetLastPInvokeError()}.");
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

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ImageBuildManifestDto
    {
        public required int SchemaVersion { get; init; }
        public required string ImageId { get; init; }
        public required Guid MachineId { get; init; }
        public required string Phase { get; init; }
        public required FileFingerprintDto WindowsIso { get; init; }
        public required FileFingerprintDto VirtioWinIso { get; init; }
        public required FileFingerprintDto GuestBridgeExecutable { get; init; }
        public required FileFingerprintDto OvmfCode { get; init; }
        public required FileFingerprintDto OvmfVariablesSource { get; init; }
        public required ToolchainDto Toolchain { get; init; }
        public required int DiskSizeGiB { get; init; }
        public required int CpuCount { get; init; }
        public required int MemoryMiB { get; init; }
        public string? ActiveHostBootId { get; init; }
        public string? PendingProcessName { get; init; }
        public required Dictionary<string, ProcessIdentityDto> ActiveProcesses { get; init; }
        public string? VerificationNonce { get; init; }
        public required VerifiedGuestEnvironmentDto? VerifiedGuestEnvironment { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required DateTimeOffset UpdatedAt { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class FileFingerprintDto
    {
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
        public required long Length { get; init; }
        public required long LastWriteUtcTicks { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ToolchainDto
    {
        public required string QemuSystem { get; init; }
        public required string QemuImg { get; init; }
        public required string Swtpm { get; init; }
        public required string RemoteViewer { get; init; }
        public required string Xorriso { get; init; }
        public required string Bubblewrap { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class ProcessIdentityDto
    {
        public required int ProcessId { get; init; }
        public required string BootId { get; init; }
        public required long StartTicks { get; init; }
        public required string ExecutablePath { get; init; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class VerifiedGuestEnvironmentDto
    {
        public required string GuestBridgeVersion { get; init; }
        public required string WindowsArchitecture { get; init; }
        public required int WindowsBuild { get; init; }
        public required string WindowsEditionId { get; init; }
        public required string WindowsDisplayVersion { get; init; }
        public required DateTimeOffset VerifiedAt { get; init; }
    }
}
