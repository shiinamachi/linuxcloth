using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.Tests.ImageBuilding;

public sealed class WindowsInstallationPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcp{Guid.NewGuid():N}"[..9]);

    [Fact]
    public void ParsesSupportedImagesAndSuggestsTheOnlyWindows11Amd64Image()
    {
        var catalog = WindowsInstallationPlanner.ParseCatalog(CreateXml(
            ImageXml(1, "Windows 11 Home ARM", "Core", 12, 26100),
            ImageXml(6, "Windows 11 Pro", "Professional", 9, 26100)));

        Assert.Equal(2, catalog.Images.Count);
        Assert.Equal(6, catalog.SuggestedImageIndex);
        var supported = Assert.Single(catalog.SupportedImages);
        Assert.Equal("amd64", supported.Architecture);
        Assert.Equal(new WindowsInstallationSelection(6, "Professional", "Windows 11 Pro"), supported.ToSelection());
    }

    [Fact]
    public void RequiresAChoiceWhenMultipleSupportedEditionsExist()
    {
        var catalog = WindowsInstallationPlanner.ParseCatalog(CreateXml(
            ImageXml(1, "Windows 11 Home", "Core", 9, 26100),
            ImageXml(6, "Windows 11 Pro", "Professional", 9, 26100)));

        Assert.Null(catalog.SuggestedImageIndex);
        Assert.Equal(2, catalog.SupportedImages.Count);
    }

    [Theory]
    [InlineData(9, 19045)]
    [InlineData(12, 26100)]
    public void RejectsMediaWithoutWindows11Amd64(int architecture, int build)
    {
        var xml = CreateXml(ImageXml(1, "Unsupported Windows", "Core", architecture, build));

        var exception = Assert.Throws<WindowsImageBuildException>(
            () => WindowsInstallationPlanner.ParseCatalog(xml));

        Assert.Contains("Windows 11 amd64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractsAndInspectsMediaInsideNetworklessConfinementThenCleansWorkspace()
    {
        var windowsIso = CreateFile("Windows; $(not-shell).iso", "iso");
        var xorriso = CreateExecutable("xorriso");
        var wimlib = CreateExecutable("wimlib-imagex");
        var bubblewrap = CreateExecutable("bwrap");
        var analysisRoot = Path.Combine(_root, "analysis");
        var runner = new PlannerRunner(
            xorriso,
            wimlib,
            CreateXml(ImageXml(6, "Windows 11 Pro", "Professional", 9, 26100)));
        var planner = new WindowsInstallationPlanner(runner, analysisRoot);

        var catalog = await planner.AnalyzeAsync(
            windowsIso,
            xorriso,
            wimlib,
            bubblewrap);

        Assert.Equal(6, catalog.SuggestedImageIndex);
        Assert.Equal(2, runner.Specs.Count);
        Assert.All(runner.Specs, spec =>
        {
            Assert.Equal(bubblewrap, spec.FileName);
            Assert.Contains("--unshare-all", spec.Arguments);
            Assert.DoesNotContain("--share-net", spec.Arguments);
            Assert.DoesNotContain("sh", spec.Arguments);
            Assert.DoesNotContain("-c", spec.Arguments);
        });
        Assert.Contains(windowsIso, runner.Specs[0].Arguments);
        Assert.Empty(Directory.EnumerateFileSystemEntries(analysisRoot));
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

    private static string CreateXml(params string[] images) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><WIM>{string.Concat(images)}</WIM>";

    private static string ImageXml(
        int index,
        string displayName,
        string editionId,
        int architecture,
        int build) =>
        $"<IMAGE INDEX=\"{index}\"><NAME>{displayName}</NAME><DISPLAYNAME>{displayName}</DISPLAYNAME>" +
        $"<WINDOWS><ARCH>{architecture}</ARCH><EDITIONID>{editionId}</EDITIONID>" +
        $"<VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>{build}</BUILD></VERSION></WINDOWS></IMAGE>";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class PlannerRunner : IProcessRunner
    {
        private readonly string _xorriso;
        private readonly string _wimlib;
        private readonly string _xml;

        public PlannerRunner(string xorriso, string wimlib, string xml)
        {
            _xorriso = xorriso;
            _wimlib = wimlib;
            _xml = xml;
        }

        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(
            ProcessSpec spec,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Specs.Add(spec);
            if (string.Equals(spec.IdentityExecutablePath, _xorriso, StringComparison.Ordinal))
            {
                var separator = spec.Arguments.ToList().IndexOf("--");
                var arguments = spec.Arguments.Skip(separator + 2).ToArray();
                var extraction = Array.IndexOf(arguments, "-extract_single");
                File.WriteAllText(arguments[extraction + 2], "test WIM");
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }

            Assert.Equal(_wimlib, spec.IdentityExecutablePath);
            return Task.FromResult(new ProcessResult(0, _xml, string.Empty));
        }
    }
}
