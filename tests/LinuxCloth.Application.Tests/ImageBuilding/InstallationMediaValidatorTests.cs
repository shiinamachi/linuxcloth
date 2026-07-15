using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.Tests.ImageBuilding;

public sealed class InstallationMediaValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm{Guid.NewGuid():N}"[..9]);

    [Fact]
    public async Task ProbesWindowsX64AndVirtioAmd64EntriesWithoutShellJoining()
    {
        var windowsIso = CreateFile("windows;touch-pwned.iso", "windows");
        var virtioIso = CreateFile("virtio.iso", "virtio");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(
        [
            "/efi/boot/bootx64.efi",
            "/sources/install.wim",
            "/vioscsi/w11/amd64/vioscsi.inf",
            "/NetKVM/w11/amd64/netkvm.inf",
        ]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var media = await validator.ValidateAsync(windowsIso, virtioIso, xorriso, bubblewrap);

        Assert.Equal(Path.GetFullPath(windowsIso), media.WindowsIso.Path);
        Assert.Equal(64, media.WindowsIso.Sha256.Length);
        Assert.Equal(4, runner.Specs.Count);
        Assert.All(runner.Specs, spec =>
        {
            Assert.Equal(bubblewrap, spec.FileName);
            Assert.Contains(xorriso, spec.Arguments);
            Assert.Contains("--unshare-all", spec.Arguments);
            Assert.DoesNotContain("--share-net", spec.Arguments);
            Assert.DoesNotContain("sh", spec.Arguments);
            Assert.DoesNotContain("-c", spec.Arguments);
        });
        Assert.Contains(runner.Specs, spec => spec.Arguments.Contains(windowsIso));
    }

    [Fact]
    public async Task ValidatesWindowsMediaIndependently()
    {
        var windowsIso = CreateFile("windows.iso", "windows");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(
        [
            "/efi/boot/bootx64.efi",
            "/sources/install.wim",
        ]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var fingerprint = await validator.ValidateWindowsAsync(windowsIso, xorriso, bubblewrap);

        Assert.Equal(Path.GetFullPath(windowsIso), fingerprint.Path);
        Assert.Equal(2, runner.Specs.Count);
        Assert.All(runner.Specs, spec => Assert.Contains(windowsIso, spec.Arguments));
    }

    [Fact]
    public async Task ValidatesVirtioMediaIndependently()
    {
        var virtioIso = CreateFile("virtio.iso", "virtio");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(
        [
            "/vioscsi/w11/amd64/vioscsi.inf",
            "/NetKVM/w11/amd64/netkvm.inf",
        ]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var fingerprint = await validator.ValidateVirtioWinAsync(virtioIso, xorriso, bubblewrap);

        Assert.Equal(Path.GetFullPath(virtioIso), fingerprint.Path);
        Assert.Equal(2, runner.Specs.Count);
        Assert.All(runner.Specs, spec => Assert.Contains(virtioIso, spec.Arguments));
    }

    [Fact]
    public async Task RejectsAnIsoWithoutAnX64UefiBootImage()
    {
        var windowsIso = CreateFile("windows.iso", "windows");
        var virtioIso = CreateFile("virtio.iso", "virtio");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(
        [
            "/sources/install.wim",
            "/vioscsi/w11/amd64/vioscsi.inf",
            "/NetKVM/w11/amd64/netkvm.inf",
        ]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var exception = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => validator.ValidateAsync(windowsIso, virtioIso, xorriso, bubblewrap));

        Assert.Contains("Windows x64", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, runner.Specs.Count);
    }

    [Fact]
    public async Task ReportsMissingWindowsInstallationImageSeparately()
    {
        var windowsIso = CreateFile("windows.iso", "windows");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(["/efi/boot/bootx64.efi"]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var exception = await Assert.ThrowsAsync<WindowsImageBuildException>(() =>
            validator.ValidateWindowsAsync(windowsIso, xorriso, bubblewrap));

        Assert.Contains("install.wim", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsVirtioMediaThatHasViostorButNotTheRequiredVioscsiDriver()
    {
        var windowsIso = CreateFile("windows.iso", "windows");
        var virtioIso = CreateFile("virtio.iso", "virtio");
        var xorriso = CreateExecutable("xorriso");
        var bubblewrap = CreateExecutable("bwrap");
        var runner = new MediaProbeRunner(
        [
            "/efi/boot/bootx64.efi",
            "/sources/install.wim",
            "/viostor/w11/amd64/viostor.inf",
            "/NetKVM/w11/amd64/netkvm.inf",
        ]);
        var validator = new XorrisoInstallationMediaValidator(runner);

        var exception = await Assert.ThrowsAsync<WindowsImageBuildException>(
            () => validator.ValidateAsync(windowsIso, virtioIso, xorriso, bubblewrap));

        Assert.Contains("storage and network drivers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeUsesFixedArgumentBoundaries()
    {
        var spec = XorrisoInstallationMediaValidator.BuildProbe(
            "/usr/bin/xorriso",
            "/tmp/a; rm -rf.iso",
            "/sources/install.wim");

        Assert.Equal("-no_rc", spec.Arguments[0]);
        Assert.Equal("/tmp/a; rm -rf.iso", spec.Arguments[9]);
        Assert.Equal("/sources/install.wim", spec.Arguments[11]);
        Assert.False(spec.InheritEnvironment);
        Assert.Equal("C", spec.Environment["LC_ALL"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name, string contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private string CreateExecutable(string name)
    {
        var path = CreateFile(name, "executable");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class MediaProbeRunner : IProcessRunner
    {
        private readonly HashSet<string> _existingEntries;

        public MediaProbeRunner(IEnumerable<string> existingEntries)
        {
            _existingEntries = new HashSet<string>(existingEntries, StringComparer.Ordinal);
        }

        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Specs.Add(spec);
            var entry = spec.Arguments[^1];
            return Task.FromResult(_existingEntries.Contains(entry)
                ? new ProcessResult(0, $"-r--r--r-- 1 0 0 4096 Jan 1 00:00 '{entry}'", string.Empty)
                : new ProcessResult(32, string.Empty, "entry not found"));
        }
    }
}
