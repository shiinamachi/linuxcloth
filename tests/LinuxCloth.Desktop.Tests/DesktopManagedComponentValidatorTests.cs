using LinuxCloth.Desktop.Services;

namespace LinuxCloth.Desktop.Tests;

public sealed class DesktopManagedComponentValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm{Guid.NewGuid():N}"[..9]);

    [Fact]
    public void AcceptsOnlyTheInstalledGuestBridge()
    {
        Directory.CreateDirectory(_root);
        var managed = Path.Combine(_root, "linuxcloth-guest-bridge.exe");
        File.WriteAllText(managed, "bridge");

        DesktopManagedComponentValidator.ValidateGuestBridge(managed, managed);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DesktopManagedComponentValidator.ValidateGuestBridge(
                managed,
                Path.Combine(_root, "untrusted.exe")));
        Assert.Contains("포함된 검증 대상", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAMissingInstalledGuestBridge()
    {
        var managed = Path.Combine(_root, "missing.exe");

        Assert.Throws<FileNotFoundException>(() =>
            DesktopManagedComponentValidator.ValidateGuestBridge(managed, managed));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
