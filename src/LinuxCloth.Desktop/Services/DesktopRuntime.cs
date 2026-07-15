using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Images;
using LinuxCloth.Application.Launching;
using LinuxCloth.Application.Storage;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qmp;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Desktop.Services;

public sealed record DesktopStartupSnapshot(
    CatalogWorkspaceState Catalog,
    IReadOnlyList<ManagedWindowsImage> Images,
    QemuDoctorResult Doctor,
    IReadOnlyList<RecoveryResult> Recovery);

public sealed class DesktopRuntime : IAsyncDisposable
{
    private readonly CatalogWorkspace _catalog;
    private readonly QemuDoctor _doctor;
    private readonly ManagedImageRegistry _images;
    private readonly LinuxClothSessionLauncher _launcher;
    private readonly LinuxClothPaths _paths;
    private readonly RecoverySessionManager _recovery;
    private bool _disposed;

    private DesktopRuntime(
        LinuxClothPaths paths,
        CatalogWorkspace catalog,
        ManagedImageRegistry images,
        QemuDoctor doctor,
        RecoverySessionManager recovery,
        LinuxClothSessionLauncher launcher)
    {
        _paths = paths;
        _catalog = catalog;
        _images = images;
        _doctor = doctor;
        _recovery = recovery;
        _launcher = launcher;
    }

    public CatalogWorkspace Catalog => _catalog;

    public ManagedImageRegistry Images => _images;

    public LinuxClothPaths Paths => _paths;

    public static DesktopRuntime CreateDefault()
    {
        var paths = LinuxClothPaths.FromEnvironment();
        var catalog = new CatalogWorkspace(paths, ResolveOfficialCatalogBundle());
        var images = new ManagedImageRegistry(paths);
        var processRunner = new SystemProcessRunner();
        var doctor = new QemuDoctor(new ExecutableLocator(), processRunner);
        var qmpConnector = new QmpConnector();
        var recovery = new RecoverySessionManager(
            new SessionRecordStore(),
            new LinuxProcessIdentityController(),
            qmpConnector);
        var host = new QemuSessionHost(new SystemProcessLauncher(), qmpConnector);
        var launcher = new LinuxClothSessionLauncher(
            paths,
            catalog,
            new DoctorLaunchPrerequisiteSource(doctor),
            new ManagedImageLaunchSource(images),
            new QemuSessionArtifactService(new SessionArtifactPreparer(processRunner)),
            new GuestConfigurationService(),
            new QemuVmSessionStarter(host));
        return new DesktopRuntime(paths, catalog, images, doctor, recovery, launcher);
    }

    public async Task<DesktopStartupSnapshot> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _paths.CreateBaseDirectories();
        var recovery = await _recovery.RecoverAllAsync(_paths.RuntimeDirectory, cancellationToken)
            .ConfigureAwait(false);
        var catalog = await _catalog.InitializeWithBundledRefreshAsync(cancellationToken)
            .ConfigureAwait(false);
        var doctor = await _doctor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        var images = await _images.ListAsync(cancellationToken).ConfigureAwait(false);
        return new DesktopStartupSnapshot(catalog, images, doctor, recovery);
    }

    public Task<QemuDoctorResult> InspectHostAsync(CancellationToken cancellationToken = default) =>
        _doctor.InspectDetailedAsync(cancellationToken);

    public Task<IReadOnlyList<ManagedWindowsImage>> ListImagesAsync(
        CancellationToken cancellationToken = default) =>
        _images.ListAsync(cancellationToken);

    public Task<IRunningLinuxClothSession> LaunchAsync(
        LaunchRequest request,
        ImageId imageId,
        IProgress<SessionState>? progress = null,
        CancellationToken cancellationToken = default) =>
        _launcher.LaunchAsync(request, imageId, progress, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _catalog.Dispose();
        return ValueTask.CompletedTask;
    }

    private static OfficialCatalogBundle ResolveOfficialCatalogBundle()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("LINUXCLOTH_CATALOG_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return CreateBundleFromRoot(configuredRoot);
        }

        var installed = Path.Combine(AppContext.BaseDirectory, "catalog");
        if (File.Exists(Path.Combine(installed, "Catalog.xml")))
        {
            return CreateBundleFromDocsDirectory(installed);
        }

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var checkout = Path.Combine(directory.FullName, "vendor", "TableClothCatalog");
            if (File.Exists(Path.Combine(checkout, "docs", "Catalog.xml")))
            {
                return OfficialCatalogBundle.FromPinnedCheckout(checkout);
            }
        }

        throw new DirectoryNotFoundException(
            "공식 TableCloth 카탈로그를 찾지 못했습니다. LINUXCLOTH_CATALOG_ROOT를 설정하세요.");
    }

    private static OfficialCatalogBundle CreateBundleFromRoot(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return File.Exists(Path.Combine(fullRoot, "docs", "Catalog.xml"))
            ? OfficialCatalogBundle.FromPinnedCheckout(fullRoot)
            : CreateBundleFromDocsDirectory(fullRoot);
    }

    private static OfficialCatalogBundle CreateBundleFromDocsDirectory(string directory) =>
        OfficialCatalogBundle.FromPinnedDocsDirectory(directory);
}
