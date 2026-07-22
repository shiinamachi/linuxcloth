using System.ComponentModel;
using System.Text.Json;
using LinuxCloth.Application.Storage;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Desktop.Services;

public enum ManagedFirmwarePreparationStatus
{
    NotRequired,
    Prepared,
    SourceFirmwareUnavailable,
    EnrollmentToolUnavailable,
    Failed,
}

public sealed record ManagedFirmwarePreparationResult(
    ManagedFirmwarePreparationStatus Status,
    string Detail);

public sealed class ManagedSecureBootFirmwareService : IDisposable
{
    private static readonly string[] RequiredSignatureDatabases = ["PK", "KEK", "db"];

    public const string EnrollmentToolName = "virt-fw-vars";
    public const string ManagedDescriptorFileName = "10-linuxcloth-secure-boot.json";
    public const long MaximumNvramBytes = 16L * 1024 * 1024;

    private readonly string _systemDescriptorDirectory;
    private readonly LinuxClothPaths _paths;
    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private bool _isPrepared;

    public ManagedSecureBootFirmwareService(
        string systemDescriptorDirectory,
        LinuxClothPaths paths,
        IExecutableLocator locator,
        IProcessRunner runner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDescriptorDirectory);
        _systemDescriptorDirectory = Path.GetFullPath(systemDescriptorDirectory);
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<ManagedFirmwarePreparationResult> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isPrepared)
            {
                return new ManagedFirmwarePreparationResult(
                    ManagedFirmwarePreparationStatus.Prepared,
                    "The managed Secure Boot variable template is ready.");
            }

            _paths.CreateBaseDirectories();
            var descriptorPath = ManagedDescriptorPath();
            var systemResolver = new FirmwareDescriptorResolver(_systemDescriptorDirectory);
            if (systemResolver.Resolve().Pair is not null)
            {
                File.Delete(descriptorPath);
                return new ManagedFirmwarePreparationResult(
                    ManagedFirmwarePreparationStatus.NotRequired,
                    "The system firmware already provides enrolled Secure Boot keys.");
            }

            File.Delete(descriptorPath);
            var source = systemResolver.ResolveSecureBootCapable().Pair;
            if (source is null)
            {
                return new ManagedFirmwarePreparationResult(
                    ManagedFirmwarePreparationStatus.SourceFirmwareUnavailable,
                    "No Q35 x86_64 firmware with Secure Boot and SMM support was found.");
            }

            if (source.NvramTemplate.SizeBytes is <= 0 or > MaximumNvramBytes)
            {
                return new ManagedFirmwarePreparationResult(
                    ManagedFirmwarePreparationStatus.Failed,
                    $"The source NVRAM template exceeds the {MaximumNvramBytes}-byte safety limit.");
            }

            var enrollmentTool = _locator.Find(EnrollmentToolName);
            if (enrollmentTool is null)
            {
                return new ManagedFirmwarePreparationResult(
                    ManagedFirmwarePreparationStatus.EnrollmentToolUnavailable,
                    $"The '{EnrollmentToolName}' executable is required to enroll Secure Boot keys.");
            }

            return await GenerateAsync(source, enrollmentTool, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            return new ManagedFirmwarePreparationResult(
                ManagedFirmwarePreparationStatus.Failed,
                $"The managed Secure Boot template could not be prepared: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task<ManagedFirmwarePreparationResult> GenerateAsync(
        FirmwarePair source,
        string enrollmentTool,
        CancellationToken cancellationToken)
    {
        var temporaryNvramPath = Path.Combine(
            _paths.FirmwareCacheDirectory,
            $".secure-boot-vars-{Guid.NewGuid():N}.tmp");
        try
        {
            var result = await _runner.RunAsync(
                    new ProcessSpec(
                        enrollmentTool,
                        [
                            "--input",
                            source.NvramTemplate.Path,
                            "--output",
                            temporaryNvramPath,
                            "--enroll-microsoft",
                            "--secure-boot",
                        ]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Failed(
                    $"'{EnrollmentToolName}' exited with code {result.ExitCode}: " +
                    BoundedDetail(result.StandardError, result.StandardOutput));
            }

            if (!File.Exists(temporaryNvramPath) ||
                File.GetAttributes(temporaryNvramPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return Failed("The enrollment tool did not create a regular NVRAM template.");
            }

            var outputLength = new FileInfo(temporaryNvramPath).Length;
            if (outputLength != source.NvramTemplate.SizeBytes ||
                outputLength is <= 0 or > MaximumNvramBytes)
            {
                return Failed(
                    $"The enrolled NVRAM template has an unexpected size ({outputLength} bytes).");
            }

            var verificationError = await VerifyEnrollmentAsync(
                    enrollmentTool,
                    temporaryNvramPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (verificationError is not null)
            {
                return Failed(verificationError);
            }

            SetPrivateFileMode(temporaryNvramPath);
            File.Move(temporaryNvramPath, _paths.ManagedFirmwareNvramPath, overwrite: true);
            SetPrivateFileMode(_paths.ManagedFirmwareNvramPath);
            await WriteDescriptorAsync(source.Executable.Path, cancellationToken).ConfigureAwait(false);

            var verified = new FirmwareDescriptorResolver(_paths.FirmwareDescriptorDirectory)
                .Resolve();
            if (verified.Pair is null)
            {
                File.Delete(ManagedDescriptorPath());
                return Failed("The generated Secure Boot firmware descriptor failed validation.");
            }

            _isPrepared = true;
            return new ManagedFirmwarePreparationResult(
                ManagedFirmwarePreparationStatus.Prepared,
                "Microsoft Secure Boot keys were enrolled in a private managed NVRAM template.");
        }
        finally
        {
            File.Delete(temporaryNvramPath);
        }
    }

    private async Task<string?> VerifyEnrollmentAsync(
        string enrollmentTool,
        string nvramPath,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
                new ProcessSpec(enrollmentTool, ["--input", nvramPath, "--print"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return $"'{EnrollmentToolName}' could not inspect the generated NVRAM template: " +
                   BoundedDetail(result.StandardError, result.StandardOutput);
        }

        var variables = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Separator: line.IndexOf(':'), Line: line))
            .Where(item => item.Separator > 0)
            .Select(item => new
            {
                Name = item.Line[..item.Separator].Trim(),
                Value = item.Line[(item.Separator + 1)..].Trim(),
            })
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var hasSignatureDatabases = RequiredSignatureDatabases
            .All(name => variables.TryGetValue(name, out var value) &&
                         value.StartsWith("blob:", StringComparison.Ordinal));
        var secureBootEnabled = variables.TryGetValue("SecureBootEnable", out var secureBoot) &&
                                string.Equals(secureBoot, "bool: ON", StringComparison.Ordinal);
        return hasSignatureDatabases && secureBootEnabled
            ? null
            : "The generated NVRAM template does not report enrolled PK, KEK, db, and enabled Secure Boot state.";
    }

    private async Task WriteDescriptorAsync(
        string firmwareExecutablePath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            _paths.FirmwareDescriptorDirectory,
            $".{ManagedDescriptorFileName}-{Guid.NewGuid():N}.tmp");
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
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("description", "linuxcloth managed Microsoft Secure Boot firmware");
                writer.WriteStartArray("interface-types");
                writer.WriteStringValue("uefi");
                writer.WriteEndArray();
                writer.WriteStartObject("mapping");
                writer.WriteString("device", "flash");
                WriteMapping(writer, "executable", firmwareExecutablePath);
                WriteMapping(writer, "nvram-template", _paths.ManagedFirmwareNvramPath);
                writer.WriteEndObject();
                writer.WriteStartArray("targets");
                writer.WriteStartObject();
                writer.WriteString("architecture", "x86_64");
                writer.WriteStartArray("machines");
                writer.WriteStringValue("pc-q35-*");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteStartArray("features");
                writer.WriteStringValue("secure-boot");
                writer.WriteStringValue("enrolled-keys");
                writer.WriteStringValue("requires-smm");
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            SetPrivateFileMode(temporaryPath);
            File.Move(temporaryPath, ManagedDescriptorPath(), overwrite: true);
            SetPrivateFileMode(ManagedDescriptorPath());
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void WriteMapping(Utf8JsonWriter writer, string name, string path)
    {
        writer.WriteStartObject(name);
        writer.WriteString("filename", path);
        writer.WriteString("format", "raw");
        writer.WriteEndObject();
    }

    private string ManagedDescriptorPath() =>
        Path.Combine(_paths.FirmwareDescriptorDirectory, ManagedDescriptorFileName);

    private static ManagedFirmwarePreparationResult Failed(string detail) =>
        new(ManagedFirmwarePreparationStatus.Failed, detail);

    private static string BoundedDetail(string standardError, string standardOutput)
    {
        var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        detail = detail.Trim();
        return detail.Length switch
        {
            0 => "no diagnostic output",
            > 2048 => detail[..2048],
            _ => detail,
        };
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
