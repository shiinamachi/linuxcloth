using System.Text.Json;
using LinuxCloth.Application.Storage;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Desktop.Tests;

public sealed class ManagedSecureBootFirmwareServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"linuxcloth-firmware-service-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnrollsKeysIntoPrivateManagedFirmwareCapability()
    {
        var fixture = CreateFixture(enrolledKeys: false);
        var runner = new EnrollmentRunner();
        using var service = new ManagedSecureBootFirmwareService(
            fixture.SystemDescriptorDirectory,
            fixture.Paths,
            new FixedLocator(fixture.EnrollmentToolPath),
            runner);

        var result = await service.PrepareAsync();

        Assert.Equal(ManagedFirmwarePreparationStatus.Prepared, result.Status);
        Assert.NotNull(runner.OutputPath);
        Assert.Equal(
            [
                "--input",
                fixture.SourceNvramPath,
                "--output",
                runner.OutputPath,
                "--enroll-microsoft",
                "--secure-boot",
            ],
            runner.EnrollmentArguments);
        Assert.Equal(1, runner.VerificationCount);
        var resolved = new FirmwareDescriptorResolver(
                [fixture.SystemDescriptorDirectory, fixture.Paths.FirmwareDescriptorDirectory])
            .Resolve();
        Assert.True(resolved.IsResolved);
        Assert.Equal(fixture.Paths.ManagedFirmwareNvramPath, resolved.Pair?.NvramTemplate.Path);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(fixture.Paths.ManagedFirmwareNvramPath));
        }
    }

    [Fact]
    public async Task ReportsMissingEnrollmentToolAsACapability()
    {
        var fixture = CreateFixture(enrolledKeys: false);
        using var service = new ManagedSecureBootFirmwareService(
            fixture.SystemDescriptorDirectory,
            fixture.Paths,
            new FixedLocator(null),
            new EnrollmentRunner());

        var result = await service.PrepareAsync();

        Assert.Equal(ManagedFirmwarePreparationStatus.EnrollmentToolUnavailable, result.Status);
        Assert.Contains(
            ManagedSecureBootFirmwareService.EnrollmentToolName,
            result.Detail,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.FirmwareDescriptorDirectory));
    }

    [Fact]
    public async Task KeepsDistributionFirmwareWhenKeysAreAlreadyEnrolled()
    {
        var fixture = CreateFixture(enrolledKeys: true);
        var runner = new EnrollmentRunner();
        using var service = new ManagedSecureBootFirmwareService(
            fixture.SystemDescriptorDirectory,
            fixture.Paths,
            new FixedLocator(fixture.EnrollmentToolPath),
            runner);

        var result = await service.PrepareAsync();

        Assert.Equal(ManagedFirmwarePreparationStatus.NotRequired, result.Status);
        Assert.Null(runner.EnrollmentArguments);
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.FirmwareDescriptorDirectory));
    }

    [Fact]
    public async Task RejectsAnUnexpectedEnrollmentOutputSize()
    {
        var fixture = CreateFixture(enrolledKeys: false);
        using var service = new ManagedSecureBootFirmwareService(
            fixture.SystemDescriptorDirectory,
            fixture.Paths,
            new FixedLocator(fixture.EnrollmentToolPath),
            new EnrollmentRunner(outputSizeDelta: 1));

        var result = await service.PrepareAsync();

        Assert.Equal(ManagedFirmwarePreparationStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(
            fixture.Paths.FirmwareDescriptorDirectory,
            ManagedSecureBootFirmwareService.ManagedDescriptorFileName)));
    }

    [Fact]
    public async Task RejectsAnOutputWithoutVerifiedSecureBootVariables()
    {
        var fixture = CreateFixture(enrolledKeys: false);
        using var service = new ManagedSecureBootFirmwareService(
            fixture.SystemDescriptorDirectory,
            fixture.Paths,
            new FixedLocator(fixture.EnrollmentToolPath),
            new EnrollmentRunner(reportValidEnrollment: false));

        var result = await service.PrepareAsync();

        Assert.Equal(ManagedFirmwarePreparationStatus.Failed, result.Status);
        Assert.Contains("PK, KEK, db", result.Detail, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private Fixture CreateFixture(bool enrolledKeys)
    {
        var systemDirectory = Path.Combine(_root, "system-firmware");
        Directory.CreateDirectory(systemDirectory);
        var codePath = Path.Combine(_root, "OVMF_CODE.fd");
        var nvramPath = Path.Combine(_root, "OVMF_VARS.fd");
        File.WriteAllBytes(codePath, new byte[4096]);
        File.WriteAllBytes(nvramPath, new byte[4096]);
        var features = enrolledKeys
            ? new[] { "secure-boot", "enrolled-keys", "requires-smm" }
            : ["secure-boot", "requires-smm"];
        var descriptor = new Dictionary<string, object?>
        {
            ["interface-types"] = new[] { "uefi" },
            ["mapping"] = new Dictionary<string, object?>
            {
                ["device"] = "flash",
                ["executable"] = new Dictionary<string, string>
                {
                    ["filename"] = codePath,
                    ["format"] = "raw",
                },
                ["nvram-template"] = new Dictionary<string, string>
                {
                    ["filename"] = nvramPath,
                    ["format"] = "raw",
                },
            },
            ["targets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["architecture"] = "x86_64",
                    ["machines"] = new[] { "pc-q35-*" },
                },
            },
            ["features"] = features,
        };
        File.WriteAllText(
            Path.Combine(systemDirectory, "50-test.json"),
            JsonSerializer.Serialize(descriptor));
        var toolPath = Path.Combine(_root, ManagedSecureBootFirmwareService.EnrollmentToolName);
        File.WriteAllText(toolPath, "test tool");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(toolPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var paths = new LinuxClothPaths(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "runtime"));
        return new Fixture(systemDirectory, nvramPath, toolPath, paths);
    }

    private sealed record Fixture(
        string SystemDescriptorDirectory,
        string SourceNvramPath,
        string EnrollmentToolPath,
        LinuxClothPaths Paths);

    private sealed class FixedLocator(string? path) : IExecutableLocator
    {
        public string? Find(string executableName)
        {
            Assert.Equal(ManagedSecureBootFirmwareService.EnrollmentToolName, executableName);
            return path;
        }
    }

    private sealed class EnrollmentRunner(
        int outputSizeDelta = 0,
        bool reportValidEnrollment = true) : IProcessRunner
    {
        public IReadOnlyList<string>? EnrollmentArguments { get; private set; }

        public string? OutputPath { get; private set; }

        public int VerificationCount { get; private set; }

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spec.Arguments.Contains("--output", StringComparer.Ordinal))
            {
                EnrollmentArguments = spec.Arguments;
                var inputPath = spec.Arguments[1];
                OutputPath = spec.Arguments[3];
                var source = File.ReadAllBytes(inputPath);
                File.WriteAllBytes(OutputPath, new byte[source.Length + outputSizeDelta]);
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            VerificationCount++;
            var output = reportValidEnrollment
                ? "PK: blob: 1 bytes\nKEK: blob: 1 bytes\ndb: blob: 1 bytes\nSecureBootEnable: bool: ON\n"
                : "SecureBootEnable: bool: OFF\n";
            return Task.FromResult(new ProcessResult(0, output, string.Empty));
        }
    }
}
