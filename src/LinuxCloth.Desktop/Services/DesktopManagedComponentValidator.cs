namespace LinuxCloth.Desktop.Services;

public static class DesktopManagedComponentValidator
{
    public static void ValidateGuestBridge(string managedPath, string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        var expected = Path.GetFullPath(managedPath);
        var actual = Path.GetFullPath(selectedPath);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "GuestBridge는 linuxcloth 패키지에 포함된 검증 대상만 사용할 수 있습니다.");
        }

        if (!File.Exists(expected))
        {
            throw new FileNotFoundException(
                "linuxcloth 패키지에 포함된 GuestBridge를 찾지 못했습니다.",
                expected);
        }
    }
}
