using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Desktop.Services;

public static class DesktopFirmwareSelectionValidator
{
    public static void Validate(
        FirmwarePair? verifiedFirmware,
        string selectedCodePath,
        string selectedVariablesPath)
    {
        if (verifiedFirmware is null)
        {
            throw new InvalidOperationException(
                "검증된 x86_64 Q35 Secure Boot OVMF 디스크립터를 찾지 못했습니다. " +
                "배포판의 QEMU 펌웨어 패키지를 설치한 뒤 시스템 검사를 다시 실행하세요.");
        }

        var selectedCode = Normalize(selectedCodePath, "OVMF 코드");
        var selectedVariables = Normalize(selectedVariablesPath, "OVMF 변수 템플릿");
        if (!string.Equals(selectedCode, verifiedFirmware.Executable.Path, StringComparison.Ordinal) ||
            !string.Equals(selectedVariables, verifiedFirmware.NvramTemplate.Path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "선택한 OVMF 파일은 Secure Boot, 등록 키, SMM이 확인된 QEMU 펌웨어 디스크립터 쌍과 일치하지 않습니다.");
        }
    }

    private static string Normalize(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{label} 경로는 절대 경로여야 합니다.");
        }

        return Path.GetFullPath(path);
    }
}
