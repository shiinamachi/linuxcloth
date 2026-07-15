using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge;

internal interface IBootstrapLauncher
{
    Task<int> LaunchAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken);
}

internal interface IProcessRunner
{
    Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}

internal sealed class BootstrapArtifactRejectedException : InvalidOperationException
{
    public BootstrapArtifactRejectedException()
        : base("The pinned bootstrap artifact was rejected.")
    {
    }
}

internal sealed class PinnedBootstrapLauncher : IBootstrapLauncher
{
    private const string BootstrapFileName = "SporkBootstrap.exe";

    private readonly IBootstrapArtifactDownloader _downloader;
    private readonly IExecutableSignatureVerifier _signatureVerifier;
    private readonly IProcessRunner _processRunner;
    private readonly IPrivateTemporaryDirectoryFactory _temporaryDirectoryFactory;

    public PinnedBootstrapLauncher(
        IBootstrapArtifactDownloader downloader,
        IExecutableSignatureVerifier signatureVerifier,
        IProcessRunner processRunner,
        IPrivateTemporaryDirectoryFactory temporaryDirectoryFactory)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _signatureVerifier = signatureVerifier ??
                             throw new ArgumentNullException(nameof(signatureVerifier));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _temporaryDirectoryFactory = temporaryDirectoryFactory ??
                                     throw new ArgumentNullException(nameof(temporaryDirectoryFactory));
    }

    public async Task<int> LaunchAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken)
    {
        var validatedServiceIds = ValidateServiceIds(serviceIds);
        cancellationToken.ThrowIfCancellationRequested();

        using var temporaryDirectory = _temporaryDirectoryFactory.Create();
        var bootstrapPath = Path.Combine(temporaryDirectory.DirectoryPath, BootstrapFileName);

        BootstrapArtifactLease artifact;
        try
        {
            artifact = await _downloader
                .DownloadAsync(bootstrapPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsArtifactAcquisitionFailure(exception))
        {
            throw new BootstrapArtifactRejectedException();
        }

        using (artifact)
        {
            ExecutableSignatureVerificationResult verificationResult;
            try
            {
                verificationResult = _signatureVerifier.Verify(
                    artifact.Path,
                    PinnedSporkRelease.BootstrapSignerCertificateSha256);
            }
            catch (Exception exception) when (IsSignatureVerificationFailure(exception))
            {
                throw new BootstrapArtifactRejectedException();
            }

            if (verificationResult is not ExecutableSignatureVerificationResult.Trusted)
            {
                throw new BootstrapArtifactRejectedException();
            }

            var startInfo = CreateStartInfo(
                artifact.Path,
                temporaryDirectory.DirectoryPath,
                validatedServiceIds);
            return await _processRunner.RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string bootstrapPath,
        string workingDirectory,
        IReadOnlyList<ServiceId> serviceIds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = bootstrapPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
            ErrorDialog = false,
        };

        startInfo.ArgumentList.Add("--zip-url-template");
        startInfo.ArgumentList.Add(PinnedSporkRelease.SporkZipUrlTemplate);
        startInfo.ArgumentList.Add("--sha256-map");
        startInfo.ArgumentList.Add(PinnedSporkRelease.SporkSha256Map);
        startInfo.ArgumentList.Add("--site-ids");
        startInfo.ArgumentList.Add(string.Join(' ', serviceIds.Select(serviceId => serviceId.Value)));
        return startInfo;
    }

    private static ServiceId[] ValidateServiceIds(IReadOnlyList<ServiceId> serviceIds)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        if (serviceIds.Count is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceIds),
                "Between 1 and 32 service identifiers are required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var copy = new ServiceId[serviceIds.Count];
        for (var index = 0; index < serviceIds.Count; index++)
        {
            var serviceId = serviceIds[index];
            if (!ServiceId.TryCreate(serviceId.Value, out var validated) || !seen.Add(validated.Value))
            {
                throw new ArgumentException(
                    "Service identifiers must be valid and unique.",
                    nameof(serviceIds));
            }

            copy[index] = validated;
        }

        return copy;
    }

    private static bool IsArtifactAcquisitionFailure(Exception exception) =>
        exception is BootstrapArtifactRejectedException or
        OperationCanceledException or
        HttpRequestException or
        IOException or
        UnauthorizedAccessException or
        CryptographicException;

    private static bool IsSignatureVerificationFailure(Exception exception) =>
        exception is Win32Exception or
        IOException or
        UnauthorizedAccessException or
        CryptographicException or
        PlatformNotSupportedException;
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The requested process did not start.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }

        return process.ExitCode;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
        catch (Win32Exception)
        {
            // Best-effort cancellation cannot safely expose process details.
        }
        catch (NotSupportedException)
        {
            // Best-effort cancellation cannot safely expose process details.
        }
    }
}
