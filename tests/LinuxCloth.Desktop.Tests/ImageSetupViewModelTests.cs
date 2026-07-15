using LinuxCloth.Application.ImageBuilding;
using LinuxCloth.Application.Images;
using LinuxCloth.Desktop.Services;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Tests;

public sealed class ImageSetupViewModelTests
{
    [Fact]
    public async Task InitializeUsesInstalledGuestBridgeAndDetectedFirmwareDefaults()
    {
        var service = new FakeImageBuildService
        {
            Defaults = new DesktopImageBuildDefaults(
                "/opt/linuxcloth/guest/linuxcloth-guest-bridge.exe",
                true,
                "/usr/share/edk2/ovmf-code.fd",
                "/usr/share/edk2/ovmf-vars.fd"),
        };
        await using var viewModel = new ImageSetupViewModel(service, _ => Task.CompletedTask);

        await viewModel.InitializeAsync();

        Assert.Equal(service.Defaults.GuestBridgeExecutablePath, viewModel.GuestBridgeExecutablePath);
        Assert.Equal(service.Defaults.OvmfCodePath, viewModel.OvmfCodePath);
        Assert.Equal(service.Defaults.OvmfVariablesTemplatePath, viewModel.OvmfVariablesTemplatePath);
    }

    [Fact]
    public async Task StartBuildPassesSelectedInputsAndRefreshesRegisteredImages()
    {
        var service = new FakeImageBuildService();
        var refreshCount = 0;
        await using var viewModel = CreateReadyViewModel(
            service,
            _ =>
            {
                refreshCount++;
                return Task.CompletedTask;
            });

        await viewModel.StartBuildAsync();

        var request = Assert.IsType<DesktopImageBuildRequest>(service.StartRequest);
        Assert.Equal("windows-11", request.ImageId.Value);
        Assert.Equal("/media/windows.iso", request.WindowsIsoPath);
        Assert.Equal("/media/virtio.iso", request.VirtioWinIsoPath);
        Assert.Equal(96, request.DiskSizeGiB);
        Assert.Equal(1, refreshCount);
        Assert.False(viewModel.IsBuilding);
        Assert.False(viewModel.HasStagingDirectory);
        Assert.Contains("등록을 완료", viewModel.BuildStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledBuildKeepsReportedStagingDirectoryForResume()
    {
        var synchronizationContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        try
        {
            var service = new FakeImageBuildService { CancelBuild = true };
            await using var viewModel = CreateReadyViewModel(service, _ => Task.CompletedTask);

            await viewModel.StartBuildAsync();

            Assert.Equal("/var/lib/linuxcloth/.staging-windows-11-test", viewModel.StagingDirectory);
            Assert.True(viewModel.CanResumeBuild);
            Assert.Contains("다시 시작", viewModel.BuildStatus, StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
        }
    }

    [Fact]
    public async Task ResumeUsesImageIdentityAndSelectedStagingDirectory()
    {
        var service = new FakeImageBuildService();
        await using var viewModel = CreateReadyViewModel(service, _ => Task.CompletedTask);
        viewModel.StagingDirectory = "/var/lib/linuxcloth/.staging-windows-11-test";

        await viewModel.ResumeBuildAsync();

        Assert.Equal("windows-11", service.ResumeImageId?.Value);
        Assert.Equal(
            "/var/lib/linuxcloth/.staging-windows-11-test",
            service.ResumeStagingDirectory);
    }

    [Fact]
    public async Task CancelAndWaitDoesNotReturnUntilActiveBuildStops()
    {
        var service = new FakeImageBuildService { WaitForCancellation = true };
        await using var viewModel = CreateReadyViewModel(service, _ => Task.CompletedTask);
        var build = viewModel.StartBuildAsync();
        Assert.True(viewModel.IsBuilding);

        viewModel.ImageIdText = "changed-during-build";
        viewModel.WindowsIsoPath = "/media/changed-windows.iso";
        viewModel.VirtioWinIsoPath = "/media/changed-virtio.iso";
        viewModel.GuestBridgeExecutablePath = "/tmp/changed-guest.exe";
        viewModel.OvmfCodePath = "/tmp/changed-code.fd";
        viewModel.OvmfVariablesTemplatePath = "/tmp/changed-vars.fd";
        viewModel.DiskSizeGiB = 128;
        viewModel.CpuCount = 8;
        viewModel.MemoryMiB = 8192;

        Assert.Equal("windows-11", viewModel.ImageIdText);
        Assert.Equal("/media/windows.iso", viewModel.WindowsIsoPath);
        Assert.Equal("/media/virtio.iso", viewModel.VirtioWinIsoPath);
        Assert.Equal(
            "/opt/linuxcloth/guest/linuxcloth-guest-bridge.exe",
            viewModel.GuestBridgeExecutablePath);
        Assert.Equal("/usr/share/edk2/ovmf-code.fd", viewModel.OvmfCodePath);
        Assert.Equal("/usr/share/edk2/ovmf-vars.fd", viewModel.OvmfVariablesTemplatePath);
        Assert.Equal(96, viewModel.DiskSizeGiB);
        Assert.Equal(4, viewModel.CpuCount);
        Assert.Equal(6144, viewModel.MemoryMiB);

        var concurrent = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.StartBuildAsync());
        Assert.Contains("이미 실행 중", concurrent.Message, StringComparison.Ordinal);

        await viewModel.CancelAndWaitAsync();
        await build;

        Assert.False(viewModel.IsBuilding);
        Assert.True(service.CancellationObserved);
    }

    [Fact]
    public async Task InvalidImageIdentityDisablesStartAndResume()
    {
        await using var viewModel = CreateReadyViewModel(
            new FakeImageBuildService(),
            _ => Task.CompletedTask);
        viewModel.ImageIdText = "Windows 11";
        viewModel.StagingDirectory = "/tmp/.staging-image";

        Assert.False(viewModel.CanStartBuild);
        Assert.False(viewModel.CanResumeBuild);
    }

    [Fact]
    public async Task PublicBuildMethodsRejectIncompleteInputs()
    {
        await using var viewModel = new ImageSetupViewModel(
            new FakeImageBuildService(),
            _ => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.StartBuildAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ResumeBuildAsync());
    }

    private static ImageSetupViewModel CreateReadyViewModel(
        IDesktopImageBuildService service,
        Func<CancellationToken, Task> onRegistered) =>
        new(service, onRegistered)
        {
            ImageIdText = "windows-11",
            WindowsIsoPath = "/media/windows.iso",
            VirtioWinIsoPath = "/media/virtio.iso",
            GuestBridgeExecutablePath = "/opt/linuxcloth/guest/linuxcloth-guest-bridge.exe",
            OvmfCodePath = "/usr/share/edk2/ovmf-code.fd",
            OvmfVariablesTemplatePath = "/usr/share/edk2/ovmf-vars.fd",
        };

    private sealed class FakeImageBuildService : IDesktopImageBuildService
    {
        public DesktopImageBuildDefaults Defaults { get; init; } =
            new("/opt/linuxcloth/guest/linuxcloth-guest-bridge.exe", true, null, null);

        public DesktopImageBuildRequest? StartRequest { get; private set; }

        public ImageId? ResumeImageId { get; private set; }

        public string? ResumeStagingDirectory { get; private set; }

        public bool CancelBuild { get; init; }

        public bool WaitForCancellation { get; init; }

        public bool CancellationObserved { get; private set; }

        public Task<DesktopImageBuildDefaults> GetImageBuildDefaultsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Defaults);

        public Task<ManagedWindowsImage> BuildImageAsync(
            DesktopImageBuildRequest request,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default)
        {
            StartRequest = request;
            if (CancelBuild || WaitForCancellation)
            {
                progress.Report(
                    new DesktopImageBuildProgress(
                        WindowsImageBuildPhase.Prepared,
                        "/var/lib/linuxcloth/.staging-windows-11-test"));
            }

            if (WaitForCancellation)
            {
                return WaitForCancellationAsync(cancellationToken);
            }

            if (CancelBuild)
            {
                return Task.FromCanceled<ManagedWindowsImage>(new CancellationToken(canceled: true));
            }

            return Task.FromResult(CreateImage(request.ImageId));
        }

        private async Task<ManagedWindowsImage> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("취소되지 않은 대기 빌드가 예기치 않게 완료되었습니다.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public Task<ManagedWindowsImage> ResumeImageBuildAsync(
            ImageId imageId,
            string stagingDirectory,
            IProgress<DesktopImageBuildProgress> progress,
            CancellationToken cancellationToken = default)
        {
            ResumeImageId = imageId;
            ResumeStagingDirectory = stagingDirectory;
            return Task.FromResult(CreateImage(imageId));
        }

        private static ManagedWindowsImage CreateImage(ImageId imageId)
        {
            var metadata = new ManagedImageMetadata(
                ManagedImageMetadata.CurrentSchemaVersion,
                imageId,
                Guid.Parse("c75580c8-566c-4496-b15a-9805386caa8e"),
                DateTimeOffset.Parse("2026-07-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                new ManagedImageFileMetadata("base", 1, 1),
                new ExternalImageFileMetadata("/firmware/code.fd", "code", 1, 1),
                new ManagedImageFileMetadata("vars", 1, 1),
                new ManagedImageTreeMetadata("tpm", 1, 1, 1),
                null);
            return new ManagedWindowsImage(
                metadata,
                "/images/test",
                "/images/test/base.qcow2",
                "/images/test/ovmf-vars.template.fd",
                "/images/test/swtpm-state.template");
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
}
