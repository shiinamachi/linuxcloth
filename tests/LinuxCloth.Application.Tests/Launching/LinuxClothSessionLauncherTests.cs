using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Application.Storage;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Sessions;
using LinuxCloth.Wsb;

namespace LinuxCloth.Application.Tests.Launching;

public sealed class LinuxClothSessionLauncherTests : IDisposable
{
    private static readonly Guid SessionId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid MachineId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lc-launch-{Guid.NewGuid():N}"[..20]);

    [Fact]
    public async Task ConnectsValidatedCatalogImageAndHostInputs()
    {
        var fixture = CreateFixture();
        var progress = new CapturingProgress();

        var session = await fixture.Launcher.LaunchAsync(
            fixture.Request,
            fixture.ImageId,
            progress);

        Assert.NotNull(fixture.Starter.Request);
        var start = fixture.Starter.Request!;
        Assert.Equal(SessionId, session.SessionId);
        Assert.Equal(SessionId, start.Configuration.SessionId);
        Assert.Equal(MachineId, start.Configuration.MachineId);
        Assert.Equal(fixture.Request, start.Configuration.Request);
        Assert.Equal("windows-11", start.ImageId);
        Assert.Equal(new string('a', 64), start.BaseImageSha256);
        Assert.Equal(fixture.Paths.RuntimeDirectory, start.Paths.RuntimeRoot);
        Assert.Equal(start.Paths.SessionDirectory, start.Confinement.SessionDirectory);
        Assert.Equal(fixture.Image.Definition.BaseImagePath, start.Confinement.BaseImagePath);
        Assert.Contains("우리은행", start.WindowTitle, StringComparison.Ordinal);
        Assert.True(start.WindowTitle.Length <= 256);
        Assert.Equal(
            [SessionState.Validating, SessionState.PreparingOverlay, SessionState.PreparingConfigDisk],
            progress.States);

        Assert.NotNull(fixture.GuestConfiguration.Manifest);
        var staged = fixture.GuestConfiguration.Manifest!;
        Assert.Equal(SessionId, staged.SessionId);
        Assert.Equal(fixture.Request.ServiceIds, staged.ServiceIds);
        Assert.Equal(new string('b', 64), staged.CatalogSha256);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 3, 0, 0, TimeSpan.Zero), staged.IssuedAtUtc);
        Assert.Equal(fixture.Catalog.CatalogPath, fixture.GuestConfiguration.CatalogPath);
        Assert.False(Directory.Exists(start.Paths.ConfigDirectory + ".unexpected"));
        Assert.True(fixture.Prerequisites.LastNetworkEnabled);
    }

    [Fact]
    public async Task RequestsOfflinePrerequisitesWhenNetworkingIsDisabled()
    {
        var fixture = CreateFixture();
        var request = new LaunchRequest(
            fixture.Request.ServiceIds,
            fixture.Request.CpuCount,
            fixture.Request.MemoryMiB,
            fixture.Request.DisplayMode,
            networkEnabled: false,
            fixture.Request.ClipboardEnabled,
            fixture.Request.UsbDeviceIds);

        _ = await fixture.Launcher.LaunchAsync(request, fixture.ImageId);

        Assert.False(fixture.Prerequisites.LastNetworkEnabled);
        Assert.Null(fixture.Starter.Request!.Configuration.PasstSocketPath);
    }

    [Theory]
    [InlineData(DisplayMode.Rdp)]
    [InlineData(DisplayMode.QemuConsole)]
    public async Task RejectsUnsupportedDisplaysBeforeResolvingResources(DisplayMode displayMode)
    {
        var fixture = CreateFixture();
        var request = new LaunchRequest(
            fixture.Request.ServiceIds,
            displayMode: displayMode);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Launcher.LaunchAsync(request, fixture.ImageId));

        Assert.Equal(0, fixture.CatalogResolver.CallCount);
        Assert.Null(fixture.Starter.Request);
    }

    [Fact]
    public async Task RejectsUsbBeforeResolvingResources()
    {
        var fixture = CreateFixture();
        var request = new LaunchRequest(
            fixture.Request.ServiceIds,
            usbDeviceIds: ["1-2"]);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Launcher.LaunchAsync(request, fixture.ImageId));

        Assert.Equal(0, fixture.CatalogResolver.CallCount);
    }

    [Fact]
    public async Task CleansPreparedArtifactsWhenGuestConfigurationFails()
    {
        var fixture = CreateFixture();
        fixture.GuestConfiguration.Failure = new IOException("catalog copy failed");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Launcher.LaunchAsync(fixture.Request, fixture.ImageId));

        var sessionDirectory = Path.Combine(
            fixture.Paths.RuntimeDirectory,
            "sessions",
            SessionId.ToString("N"));
        Assert.False(Directory.Exists(sessionDirectory));
        Assert.Null(fixture.Starter.Request);
    }

    [Fact]
    public async Task LeavesCleanupToTheSessionHostAfterOwnershipTransfer()
    {
        var fixture = CreateFixture();
        fixture.Starter.Failure = new IOException("host preserved an owned process");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Launcher.LaunchAsync(fixture.Request, fixture.ImageId));

        Assert.NotNull(fixture.Starter.Request);
        var start = fixture.Starter.Request!;
        Assert.True(Directory.Exists(start.Paths.SessionDirectory));
    }

    [Fact]
    public async Task RejectsCatalogResolutionForAnotherSelection()
    {
        var fixture = CreateFixture();
        fixture.CatalogResolver.Resolution = new LaunchCatalogResolution(
            [ServiceId.Parse("KookminBank")],
            fixture.Catalog.CatalogPath,
            fixture.Catalog.CatalogSha256,
            ["KB국민은행"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Launcher.LaunchAsync(fixture.Request, fixture.ImageId));

        Assert.Null(fixture.Starter.Request);
    }

    [Fact]
    public async Task RejectsDoctorRuntimeOutsideTheOwnedXdgDirectory()
    {
        var fixture = CreateFixture();
        fixture.Prerequisites.Value = fixture.Prerequisites.Value with
        {
            RuntimeDirectory = Path.Combine(_root, "somewhere-else"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Launcher.LaunchAsync(fixture.Request, fixture.ImageId));

        Assert.Null(fixture.Starter.Request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private Fixture CreateFixture()
    {
        var paths = new LinuxClothPaths(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "run"));
        var serviceIds = new[] { ServiceId.Parse("WooriBank") };
        var request = new LaunchRequest(serviceIds);
        var imageId = ImageId.Parse("windows-11");
        var catalog = new LaunchCatalogResolution(
            serviceIds,
            Path.Combine(_root, "Catalog.xml"),
            new string('b', 64),
            ["우리은행"]);
        var catalogResolver = new FakeCatalogResolver(catalog);
        var prerequisites = new FakePrerequisiteSource(CreatePrerequisites(paths.RuntimeDirectory));
        var image = new VerifiedLaunchImage(
            imageId,
            new SessionImageDefinition(
                imageId.Value,
                MachineId,
                Path.Combine(_root, "base.qcow2"),
                Path.Combine(_root, "OVMF_CODE.fd"),
                Path.Combine(_root, "OVMF_VARS.fd"),
                Path.Combine(_root, "swtpm")),
            new string('a', 64));
        var imageSource = new FakeImageSource(image);
        var artifacts = new FakeArtifactService();
        var guestConfiguration = new FakeGuestConfigurationService();
        var starter = new FakeSessionStarter();
        var launcher = new LinuxClothSessionLauncher(
            paths,
            catalogResolver,
            prerequisites,
            imageSource,
            artifacts,
            guestConfiguration,
            starter,
            new FixedTimeProvider(),
            () => SessionId);
        return new Fixture(
            launcher,
            paths,
            request,
            imageId,
            catalog,
            catalogResolver,
            prerequisites,
            image,
            guestConfiguration,
            starter);
    }

    private static QemuLaunchPrerequisites CreatePrerequisites(string runtimeDirectory) =>
        new(
            new QemuToolchain(
                "/usr/bin/qemu-system-x86_64",
                "/usr/bin/qemu-img",
                "/usr/bin/swtpm",
                "/usr/bin/passt",
                "/usr/bin/remote-viewer"),
            new FirmwarePair(
                "/usr/share/qemu/firmware/secure.json",
                new FirmwareImage("/usr/share/edk2/OVMF_CODE.fd", 1),
                new FirmwareImage("/usr/share/edk2/OVMF_VARS.fd", 1)),
            "/usr/bin/bwrap",
            runtimeDirectory);

    private sealed record Fixture(
        LinuxClothSessionLauncher Launcher,
        LinuxClothPaths Paths,
        LaunchRequest Request,
        ImageId ImageId,
        LaunchCatalogResolution Catalog,
        FakeCatalogResolver CatalogResolver,
        FakePrerequisiteSource Prerequisites,
        VerifiedLaunchImage Image,
        FakeGuestConfigurationService GuestConfiguration,
        FakeSessionStarter Starter);

    private sealed class FakeCatalogResolver(LaunchCatalogResolution resolution) : ILaunchCatalogResolver
    {
        public LaunchCatalogResolution Resolution { get; set; } = resolution;

        public int CallCount { get; private set; }

        public Task<LaunchCatalogResolution> ResolveAsync(
            IReadOnlyList<ServiceId> serviceIds,
            CancellationToken cancellationToken = default)
        {
            _ = serviceIds;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(Resolution);
        }
    }

    private sealed class FakePrerequisiteSource(QemuLaunchPrerequisites value) : ILaunchPrerequisiteSource
    {
        public QemuLaunchPrerequisites Value { get; set; } = value;

        public bool LastNetworkEnabled { get; private set; }

        public Task<QemuLaunchPrerequisites> ResolveAsync(
            bool networkEnabled,
            CancellationToken cancellationToken = default)
        {
            LastNetworkEnabled = networkEnabled;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
    }

    private sealed class FakeImageSource(VerifiedLaunchImage image) : ILaunchImageSource
    {
        public Task<VerifiedLaunchImage> ResolveAsync(
            ImageId imageId,
            CancellationToken cancellationToken = default)
        {
            _ = imageId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(image);
        }
    }

    private sealed class FakeArtifactService : ISessionArtifactService
    {
        public Task PrepareAsync(
            SessionPaths paths,
            SessionImageDefinition image,
            string qemuImgPath,
            CancellationToken cancellationToken = default)
        {
            _ = image;
            _ = qemuImgPath;
            cancellationToken.ThrowIfCancellationRequested();
            paths.CreateDirectories();
            File.WriteAllText(paths.OverlayPath, "overlay");
            File.WriteAllText(paths.OvmfVariablesPath, "vars");
            Directory.CreateDirectory(paths.SwtpmStateDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuestConfigurationService : IGuestConfigurationService
    {
        public GuestLaunchManifest? Manifest { get; private set; }

        public string? CatalogPath { get; private set; }

        public Exception? Failure { get; set; }

        public Task StageAsync(
            string destinationDirectory,
            GuestLaunchManifest manifest,
            string expressWsb,
            string catalogSnapshotPath,
            CancellationToken cancellationToken = default)
        {
            _ = expressWsb;
            cancellationToken.ThrowIfCancellationRequested();
            Manifest = manifest;
            CatalogPath = catalogSnapshotPath;
            if (Failure is not null)
            {
                throw Failure;
            }

            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllText(Path.Combine(destinationDirectory, "launch.json"), "staged");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionStarter : IVmSessionStarter
    {
        public QemuSessionStartRequest? Request { get; private set; }

        public Exception? Failure { get; set; }

        public Task<IRunningLinuxClothSession> StartAsync(
            QemuSessionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Failure is null
                ? Task.FromResult<IRunningLinuxClothSession>(new FakeRunningSession(request.Paths.SessionId))
                : throw Failure;
        }
    }

    private sealed class FakeRunningSession(Guid sessionId) : IRunningLinuxClothSession
    {
        public Guid SessionId { get; } = sessionId;

        public Task Completion => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingProgress : IProgress<SessionState>
    {
        public List<SessionState> States { get; } = [];

        public void Report(SessionState value) => States.Add(value);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 7, 15, 3, 0, 0, TimeSpan.Zero);
    }
}
