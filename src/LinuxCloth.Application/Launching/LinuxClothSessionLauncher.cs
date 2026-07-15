using LinuxCloth.Application.Images;
using LinuxCloth.Application.Storage;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Confinement;
using LinuxCloth.Runtime.Qemu.Qemu;
using LinuxCloth.Runtime.Qemu.Sessions;
using LinuxCloth.Wsb;

namespace LinuxCloth.Application.Launching;

public sealed class LinuxClothSessionLauncher
{
    private const int MaximumWindowTitleLength = 256;

    private readonly LinuxClothPaths _paths;
    private readonly ILaunchCatalogResolver _catalogResolver;
    private readonly ILaunchPrerequisiteSource _prerequisiteSource;
    private readonly ILaunchImageSource _imageSource;
    private readonly ISessionArtifactService _artifactService;
    private readonly IGuestConfigurationService _guestConfigurationService;
    private readonly IVmSessionStarter _sessionStarter;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _sessionIdFactory;

    public LinuxClothSessionLauncher(
        LinuxClothPaths paths,
        ILaunchCatalogResolver catalogResolver,
        ILaunchPrerequisiteSource prerequisiteSource,
        ILaunchImageSource imageSource,
        ISessionArtifactService artifactService,
        IGuestConfigurationService guestConfigurationService,
        IVmSessionStarter sessionStarter,
        TimeProvider? timeProvider = null,
        Func<Guid>? sessionIdFactory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _catalogResolver = catalogResolver ?? throw new ArgumentNullException(nameof(catalogResolver));
        _prerequisiteSource = prerequisiteSource ?? throw new ArgumentNullException(nameof(prerequisiteSource));
        _imageSource = imageSource ?? throw new ArgumentNullException(nameof(imageSource));
        _artifactService = artifactService ?? throw new ArgumentNullException(nameof(artifactService));
        _guestConfigurationService = guestConfigurationService ??
                                     throw new ArgumentNullException(nameof(guestConfigurationService));
        _sessionStarter = sessionStarter ?? throw new ArgumentNullException(nameof(sessionStarter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionIdFactory = sessionIdFactory ?? Guid.NewGuid;
    }

    public async Task<IRunningLinuxClothSession> LaunchAsync(
        LaunchRequest request,
        ImageId imageId,
        IProgress<SessionState>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ImageId.ValidateInitialized(imageId);
        try
        {
            return await LaunchCoreAsync(request, imageId, progress, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            progress?.Report(SessionState.Failed);
            throw;
        }
    }

    private async Task<IRunningLinuxClothSession> LaunchCoreAsync(
        LaunchRequest request,
        ImageId imageId,
        IProgress<SessionState>? progress,
        CancellationToken cancellationToken)
    {
        ValidateSupportedRequest(request);
        progress?.Report(SessionState.Validating);
        _paths.CreateBaseDirectories();

        var catalogTask = _catalogResolver.ResolveAsync(request.ServiceIds, cancellationToken);
        var prerequisitesTask = _prerequisiteSource.ResolveAsync(
            request.NetworkEnabled,
            cancellationToken);
        var imageTask = _imageSource.ResolveAsync(imageId, cancellationToken);
        await Task.WhenAll(catalogTask, prerequisitesTask, imageTask).ConfigureAwait(false);

        var catalog = await catalogTask.ConfigureAwait(false);
        var prerequisites = await prerequisitesTask.ConfigureAwait(false);
        var image = await imageTask.ConfigureAwait(false);
        ValidateResolvedInputs(request, imageId, catalog, prerequisites.RuntimeDirectory, image);

        var sessionId = _sessionIdFactory();
        if (sessionId == Guid.Empty)
        {
            throw new InvalidOperationException("The session identifier source returned an empty UUID.");
        }

        var paths = SessionPaths.Create(_paths.RuntimeDirectory, sessionId);
        var sessionHostOwnsCleanup = false;
        try
        {
            progress?.Report(SessionState.PreparingOverlay);
            await _artifactService.PrepareAsync(
                    paths,
                    image.Definition,
                    prerequisites.Toolchain.QemuImg,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(SessionState.PreparingConfigDisk);
            RemoveEmptyConfigurationDirectory(paths.ConfigDirectory);
            var manifest = new GuestLaunchManifest(
                sessionId,
                request.ServiceIds,
                catalog.CatalogSha256,
                request.NetworkEnabled,
                request.ClipboardEnabled,
                _timeProvider.GetUtcNow());
            var expressWsb = ExpressWsbGenerator.Generate(
                request.ServiceIds,
                request.NetworkEnabled,
                request.ClipboardEnabled,
                request.MemoryMiB);
            await _guestConfigurationService.StageAsync(
                    paths.ConfigDirectory,
                    manifest,
                    expressWsb,
                    catalog.CatalogPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var configuration = new QemuLaunchConfiguration(
                prerequisites.Toolchain,
                request,
                sessionId,
                image.Definition.MachineId,
                paths.SessionDirectory,
                paths.OverlayPath,
                image.Definition.OvmfCodePath,
                paths.OvmfVariablesPath,
                paths.SwtpmSocketPath,
                paths.QmpSocketPath,
                paths.SpiceSocketPath,
                paths.GuestBridgeSocketPath,
                paths.ConfigDirectory,
                request.NetworkEnabled ? paths.PasstSocketPath : null);
            var confinement = new BubblewrapQemuConfinementOptions(
                prerequisites.Bubblewrap,
                paths.SessionDirectory,
                image.Definition.BaseImagePath,
                image.Definition.OvmfCodePath);

            sessionHostOwnsCleanup = true;
            return await _sessionStarter.StartAsync(
                    new QemuSessionStartRequest(
                        configuration,
                        paths,
                        BuildWindowTitle(catalog.DisplayNames),
                        image.ImageId.Value,
                        image.BaseImageSha256,
                        confinement,
                        progress),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception launchFailure)
        {
            if (!sessionHostOwnsCleanup)
            {
                try
                {
                    SessionCleaner.Delete(paths);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Session launch failed and its unowned artifacts could not be removed.",
                        launchFailure,
                        cleanupFailure);
                }
            }

            throw;
        }
    }

    private static void ValidateSupportedRequest(LaunchRequest request)
    {
        if (request.DisplayMode != DisplayMode.Spice)
        {
            throw new NotSupportedException("The secure launch path currently supports the SPICE console only.");
        }

        if (request.UsbDeviceIds.Count > 0)
        {
            throw new NotSupportedException("USB redirection requires a device-scoped policy and is not enabled yet.");
        }
    }

    private void ValidateResolvedInputs(
        LaunchRequest request,
        ImageId requestedImageId,
        LaunchCatalogResolution catalog,
        string runtimeDirectory,
        VerifiedLaunchImage image)
    {
        if (!catalog.ServiceIds.SequenceEqual(request.ServiceIds))
        {
            throw new InvalidOperationException("The catalog resolved a different service selection.");
        }

        if (image.ImageId != requestedImageId ||
            !string.Equals(image.Definition.ImageId, requestedImageId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The image source resolved a different managed image.");
        }

        if (image.Definition.MachineId == Guid.Empty ||
            image.BaseImageSha256.Length != 64 ||
            image.BaseImageSha256.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("The verified image metadata is invalid.");
        }

        if (!string.Equals(
                Path.GetFullPath(runtimeDirectory),
                Path.GetFullPath(_paths.RuntimeDirectory),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The doctor runtime directory does not match the application's secured XDG runtime directory.");
        }
    }

    private static string BuildWindowTitle(IReadOnlyList<string> displayNames)
    {
        var serviceTitle = string.Join(", ", displayNames);
        const string prefix = "linuxcloth — ";
        const string suffix = " — 창을 닫으면 세션 데이터가 삭제됩니다";
        var maximumServiceLength = MaximumWindowTitleLength - prefix.Length - suffix.Length;
        if (serviceTitle.Length > maximumServiceLength)
        {
            serviceTitle = $"{serviceTitle[..(maximumServiceLength - 1)]}…";
        }

        return $"{prefix}{serviceTitle}{suffix}";
    }

    private static void RemoveEmptyConfigurationDirectory(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException("The prepared session configuration directory is missing or not empty.");
        }

        Directory.Delete(path);
    }

}
