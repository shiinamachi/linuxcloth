using LinuxCloth.Desktop.Services;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Tests;

public sealed class DesktopFirmwareSelectionValidatorTests
{
    private static readonly FirmwarePair VerifiedFirmware = new(
        "/usr/share/qemu/firmware/50-edk2.json",
        new FirmwareImage("/usr/share/edk2/ovmf-code.fd", 4096),
        new FirmwareImage("/usr/share/edk2/ovmf-vars.fd", 8192));

    [Fact]
    public void AcceptsExactPairFromCompatibleFirmwareDescriptor()
    {
        DesktopFirmwareSelectionValidator.Validate(
            VerifiedFirmware,
            VerifiedFirmware.Executable.Path,
            VerifiedFirmware.NvramTemplate.Path);
    }

    [Theory]
    [InlineData("/tmp/arbitrary-code.fd", "/usr/share/edk2/ovmf-vars.fd")]
    [InlineData("/usr/share/edk2/ovmf-code.fd", "/tmp/arbitrary-vars.fd")]
    public void RejectsFilesOutsideCompatibleFirmwareDescriptor(
        string selectedCode,
        string selectedVariables)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DesktopFirmwareSelectionValidator.Validate(
                VerifiedFirmware,
                selectedCode,
                selectedVariables));

        Assert.Contains("디스크립터 쌍", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsSelectionWhenNoCompatibleDescriptorWasFound()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => DesktopFirmwareSelectionValidator.Validate(
                null,
                "/tmp/code.fd",
                "/tmp/vars.fd"));

        Assert.Contains("펌웨어 패키지", exception.Message, StringComparison.Ordinal);
    }
}
