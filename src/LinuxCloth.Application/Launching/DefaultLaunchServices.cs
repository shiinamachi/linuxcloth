using LinuxCloth.Application.Images;
using LinuxCloth.Runtime.Qemu.Doctor;
using LinuxCloth.Runtime.Qemu.Sessions;
using LinuxCloth.Wsb;

namespace LinuxCloth.Application.Launching;

public sealed class DoctorLaunchPrerequisiteSource : ILaunchPrerequisiteSource
{
    private readonly QemuDoctor _doctor;

    public DoctorLaunchPrerequisiteSource(QemuDoctor doctor)
    {
        _doctor = doctor ?? throw new ArgumentNullException(nameof(doctor));
    }

    public async Task<QemuLaunchPrerequisites> ResolveAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _doctor.InspectDetailedAsync(cancellationToken).ConfigureAwait(false);
        return result.LaunchPrerequisites ?? throw new LaunchPrerequisiteException(result.Report);
    }
}

public sealed class ManagedImageLaunchSource : ILaunchImageSource
{
    private readonly ManagedImageRegistry _registry;

    public ManagedImageLaunchSource(ManagedImageRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task<VerifiedLaunchImage> ResolveAsync(
        ImageId imageId,
        CancellationToken cancellationToken = default)
    {
        var image = await _registry.LoadAsync(imageId, cancellationToken).ConfigureAwait(false);
        var verification = await _registry.VerifyAsync(imageId, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw new ImageVerificationException(verification);
        }

        return new VerifiedLaunchImage(
            imageId,
            image.ToSessionImageDefinition(),
            image.Metadata.BaseImage.Sha256);
    }
}

public sealed class QemuSessionArtifactService : ISessionArtifactService
{
    private readonly SessionArtifactPreparer _preparer;

    public QemuSessionArtifactService(SessionArtifactPreparer preparer)
    {
        _preparer = preparer ?? throw new ArgumentNullException(nameof(preparer));
    }

    public Task PrepareAsync(
        SessionPaths paths,
        SessionImageDefinition image,
        string qemuImgPath,
        CancellationToken cancellationToken = default) =>
        _preparer.PrepareAsync(paths, image, qemuImgPath, cancellationToken);
}

public sealed class GuestConfigurationService : IGuestConfigurationService
{
    public Task StageAsync(
        string destinationDirectory,
        GuestLaunchManifest manifest,
        string expressWsb,
        string catalogSnapshotPath,
        CancellationToken cancellationToken = default) =>
        GuestConfigStager.StageAsync(
            destinationDirectory,
            manifest,
            expressWsb,
            catalogSnapshotPath,
            cancellationToken);
}

public sealed class QemuVmSessionStarter : IVmSessionStarter
{
    private readonly QemuSessionHost _host;

    public QemuVmSessionStarter(QemuSessionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task<IRunningLinuxClothSession> StartAsync(
        QemuSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var runningSession = await _host.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return new QemuRunningLinuxClothSession(runningSession);
    }

    private sealed class QemuRunningLinuxClothSession : IRunningLinuxClothSession
    {
        private readonly QemuRunningSession _inner;
        private readonly Task _completion;
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private bool _disposed;

        public QemuRunningLinuxClothSession(QemuRunningSession inner)
        {
            _inner = inner;
            _completion = StopWhenDisplayClosesAsync();
        }

        public Guid SessionId => _inner.SessionId;

        public Task Completion => _completion;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _disposeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    await _inner.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await _completion.ConfigureAwait(false);
                }
                finally
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                    _disposed = true;
                }
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        private async Task StopWhenDisplayClosesAsync()
        {
            try
            {
                var displayExit = _inner.WaitForDisplayExitAsync(CancellationToken.None);
                var qemuExit = _inner.WaitForExitAsync(CancellationToken.None);
                var completed = await Task.WhenAny(displayExit, qemuExit).ConfigureAwait(false);
                _ = await completed.ConfigureAwait(false);
            }
            finally
            {
                await _inner.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
