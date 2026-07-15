using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SessionArtifactPreparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lc-{Guid.NewGuid():N}");

    [Fact]
    public async Task UsesQcow2BackingFileWithoutCommittingBase()
    {
        Directory.CreateDirectory(_root);
        var image = CreateImage();
        var paths = SessionPaths.Create(Path.Combine(_root, "run"), Guid.NewGuid());
        var runner = new RecordingProcessRunner(paths.OverlayPath);

        await new SessionArtifactPreparer(runner).PrepareAsync(
            paths,
            image,
            "/usr/bin/qemu-img",
            "/usr/bin/bwrap");

        var spec = Assert.Single(runner.Specs);
        Assert.Equal("/usr/bin/bwrap", spec.FileName);
        Assert.Equal("/usr/bin/qemu-img", spec.IdentityExecutablePath);
        Assert.Contains("--unshare-all", spec.Arguments);
        Assert.DoesNotContain("--share-net", spec.Arguments);
        var separator = spec.Arguments.ToList().IndexOf("--");
        Assert.Equal(
            ["create", "-q", "-f", "qcow2", "-F", "qcow2", "-b", image.BaseImagePath, paths.OverlayPath],
            spec.Arguments.Skip(separator + 2));
        Assert.True(File.Exists(paths.OvmfVariablesPath));
        Assert.True(File.Exists(Path.Combine(paths.SwtpmStateDirectory, "tpm2-00.permall")));
    }

    [Fact]
    public async Task FailedOverlayCreationCleansPartialSession()
    {
        Directory.CreateDirectory(_root);
        var image = CreateImage();
        var paths = SessionPaths.Create(Path.Combine(_root, "run"), Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SessionArtifactPreparer(new FailingProcessRunner()).PrepareAsync(
                paths,
                image,
                "/usr/bin/qemu-img",
                "/usr/bin/bwrap"));

        Assert.False(Directory.Exists(paths.SessionDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionImageDefinition CreateImage()
    {
        var imageDirectory = Path.Combine(_root, "image");
        var tpmDirectory = Path.Combine(imageDirectory, "tpm");
        Directory.CreateDirectory(tpmDirectory);
        var baseImage = WriteFile(imageDirectory, "base.qcow2");
        var ovmfCode = WriteFile(imageDirectory, "OVMF_CODE.fd");
        var ovmfVariables = WriteFile(imageDirectory, "OVMF_VARS.fd");
        WriteFile(tpmDirectory, "tpm2-00.permall");
        return new SessionImageDefinition("test", Guid.NewGuid(), baseImage, ovmfCode, ovmfVariables, tpmDirectory);
    }

    private static string WriteFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    private sealed class RecordingProcessRunner(string overlayPath) : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
        {
            Specs.Add(spec);
            File.WriteAllText(overlayPath, "overlay");
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FailingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "failed"));
    }
}
