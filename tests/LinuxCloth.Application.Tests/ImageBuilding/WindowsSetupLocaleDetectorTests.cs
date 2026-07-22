using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.Tests.ImageBuilding;

public sealed class WindowsSetupLocaleDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcl{Guid.NewGuid():N}"[..9]);

    [Fact]
    public void ParsesAndCanonicalizesTheFirstAvailableUiLanguage()
    {
        var locale = WindowsSetupLocaleDetector.ParseLangIni(
            """
            [Available UI Languages]
            ko-kr = 3

            [Fallback Languages]
            ko-kr = en-us
            """);

        Assert.Equal("ko-KR", locale);
    }

    [Fact]
    public void RejectsMetadataWithoutAnAvailableUiLanguage()
    {
        var exception = Assert.Throws<WindowsImageBuildException>(() =>
            WindowsSetupLocaleDetector.ParseLangIni(
                """
                [Fallback Languages]
                ko-kr = en-us
                """));

        Assert.Contains("supported UI language", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsLanguageMetadataInsideNetworklessConfinement()
    {
        var windowsIso = CreateFile("Windows; $(not-shell).iso", "iso");
        var sevenZip = CreateExecutable("7z");
        var bubblewrap = CreateExecutable("bwrap");
        var extractionDirectory = Path.Combine(_root, "locale");
        Directory.CreateDirectory(extractionDirectory);
        var runner = new LocaleExtractionRunner();
        var detector = new WindowsSetupLocaleDetector(runner);

        var locale = await detector.DetectAsync(
            windowsIso,
            sevenZip,
            bubblewrap,
            extractionDirectory);

        Assert.Equal("ko-KR", locale);
        var spec = Assert.Single(runner.Specs);
        Assert.Equal(bubblewrap, spec.FileName);
        Assert.Contains("--unshare-all", spec.Arguments);
        Assert.DoesNotContain("--share-net", spec.Arguments);
        Assert.DoesNotContain("sh", spec.Arguments);
        Assert.DoesNotContain("-c", spec.Arguments);
        Assert.Contains(windowsIso, spec.Arguments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(extractionDirectory));
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

    private sealed class LocaleExtractionRunner : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Specs.Add(spec);
            var output = spec.Arguments.Single(argument => argument.StartsWith("-o", StringComparison.Ordinal))[2..];
            File.WriteAllText(
                Path.Combine(output, "lang.ini"),
                "[Available UI Languages]\r\nko-kr = 3\r\n");
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }
}
