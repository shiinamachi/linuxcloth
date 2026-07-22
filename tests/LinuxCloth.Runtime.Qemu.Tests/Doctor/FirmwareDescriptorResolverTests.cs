using System.Text;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Runtime.Qemu.Tests.Doctor;

public sealed class FirmwareDescriptorResolverTests
{
    [Fact]
    public void ResolvesValidX64Q35SecureBootFirmwarePair()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteFirmwareFiles(4096, 8192);
        var descriptorPath = fixture.WriteDescriptor();

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.True(result.IsResolved);
        Assert.Empty(result.Diagnostics);
        var pair = Assert.IsType<FirmwarePair>(result.Pair);
        Assert.Equal(descriptorPath, pair.DescriptorPath);
        Assert.Equal(fixture.ExecutablePath, pair.Executable.Path);
        Assert.Equal(fixture.NvramTemplatePath, pair.NvramTemplate.Path);
        Assert.Equal(4096, pair.Executable.SizeBytes);
        Assert.Equal(8192, pair.NvramTemplate.SizeBytes);
    }

    [Fact]
    public void RejectsDescriptorMissingRequiredSecureBootFeatures()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteDescriptor(features: ["secure-boot", "requires-smm"]);

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.Null(result.Pair);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == FirmwareDiagnosticCode.MissingRequiredFeatures);
        Assert.Contains("enrolled-keys", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesSecureBootCapableFirmwareBeforeKeysAreEnrolled()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteDescriptor(features: ["secure-boot", "requires-smm"]);

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory)
            .ResolveSecureBootCapable();

        Assert.True(result.IsResolved);
        Assert.NotNull(result.Pair);
    }

    [Fact]
    public void ResolvesFirmwareAcrossSystemAndManagedDescriptorDirectories()
    {
        using var system = new FirmwareDescriptorFixture();
        using var managed = new FirmwareDescriptorFixture();
        system.WriteDescriptor(features: ["secure-boot", "requires-smm"]);
        var managedDescriptor = managed.WriteDescriptor();

        var result = new FirmwareDescriptorResolver(
                [system.DescriptorDirectory, managed.DescriptorDirectory])
            .Resolve();

        Assert.True(result.IsResolved);
        Assert.Equal(managedDescriptor, result.Pair?.DescriptorPath);
    }

    [Fact]
    public void RejectsRelativeAndTraversalMappingPaths()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteDescriptor(
            name: "10-relative.json",
            executablePath: "../OVMF_CODE.fd");
        fixture.WriteDescriptor(
            name: "20-traversal.json",
            executablePath: Path.Combine(fixture.DescriptorDirectory, "..", "OVMF_CODE.fd"));

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.Null(result.Pair);
        Assert.Equal(
            2,
            result.Diagnostics.Count(
                diagnostic => diagnostic.Code == FirmwareDiagnosticCode.UnsafeMappingPath));
    }

    [Fact]
    public void RejectsOversizedAndInvalidJsonDescriptorsWithTypedDiagnostics()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteRawDescriptor(
            "10-oversized.json",
            new byte[FirmwareDescriptorResolver.MaximumDescriptorBytes + 1]);
        fixture.WriteRawDescriptor(
            "20-invalid.json",
            Encoding.UTF8.GetBytes("{\"mapping\":"));

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.Null(result.Pair);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == FirmwareDiagnosticCode.DescriptorTooLarge);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == FirmwareDiagnosticCode.InvalidJson);
    }

    [Fact]
    public void RejectsMissingFirmwareImage()
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteDescriptor(executablePath: Path.Combine(fixture.DescriptorDirectory, "missing.fd"));

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.Null(result.Pair);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == FirmwareDiagnosticCode.FirmwareFileMissing);
    }

    [Theory]
    [InlineData("aarch64", "pc-q35-*")]
    [InlineData("x86_64", "pc-i440fx-*")]
    public void RejectsIncompatibleArchitectureOrMachine(string architecture, string machine)
    {
        using var fixture = new FirmwareDescriptorFixture();
        fixture.WriteDescriptor(architecture: architecture, machines: [machine]);

        var result = new FirmwareDescriptorResolver(fixture.DescriptorDirectory).Resolve();

        Assert.Null(result.Pair);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == FirmwareDiagnosticCode.UnsupportedTarget);
    }
}
