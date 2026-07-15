using System.Security.Cryptography;
using System.Text;
using LinuxCloth.Wsb;

namespace LinuxCloth.GuestBridge;

internal enum ConfigResolutionStatus
{
    Success,
    NotFound,
    Invalid,
    Ambiguous,
}

internal sealed record ConfigResolution(
    ConfigResolutionStatus Status,
    GuestLaunchManifest? Manifest = null);

internal sealed class GuestConfigResolver
{
    private const int MaximumCatalogBytes = 16 * 1024 * 1024;

    private readonly IConfigDriveProvider _driveProvider;
    private readonly IDiagnosticLog _diagnosticLog;

    public GuestConfigResolver(
        IConfigDriveProvider driveProvider,
        IDiagnosticLog diagnosticLog)
    {
        _driveProvider = driveProvider ?? throw new ArgumentNullException(nameof(driveProvider));
        _diagnosticLog = diagnosticLog ?? throw new ArgumentNullException(nameof(diagnosticLog));
    }

    public ConfigResolution Resolve()
    {
        var validManifests = new List<GuestLaunchManifest>();
        var candidateCount = 0;

        foreach (var root in _driveProvider.GetReadyDriveRoots())
        {
            if (!TryGetCandidatePaths(root, out var paths))
            {
                continue;
            }

            candidateCount++;
            if (TryValidate(paths, out var manifest))
            {
                validManifests.Add(manifest);
            }
            else
            {
                _diagnosticLog.Write(DiagnosticEvent.ConfigurationRejected);
            }
        }

        return validManifests.Count switch
        {
            1 => new ConfigResolution(ConfigResolutionStatus.Success, validManifests[0]),
            > 1 => new ConfigResolution(ConfigResolutionStatus.Ambiguous),
            _ when candidateCount > 0 => new ConfigResolution(ConfigResolutionStatus.Invalid),
            _ => new ConfigResolution(ConfigResolutionStatus.NotFound),
        };
    }

    private static bool TryGetCandidatePaths(string root, out ConfigPaths paths)
    {
        paths = default;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var manifestPath = Path.Combine(root, GuestConfigStager.LaunchManifestFileName);
            var sidecarPath = Path.Combine(root, GuestConfigStager.LaunchManifestHashFileName);
            var catalogPath = Path.Combine(root, GuestConfigStager.CatalogFileName);
            paths = new ConfigPaths(manifestPath, sidecarPath, catalogPath);
            return File.Exists(manifestPath) || File.Exists(sidecarPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidate(ConfigPaths paths, out GuestLaunchManifest manifest)
    {
        manifest = null!;

        try
        {
            EnsurePlainFile(paths.ManifestPath);
            EnsurePlainFile(paths.SidecarPath);

            var manifestBytes = ReadBoundedFile(
                paths.ManifestPath,
                GuestLaunchManifestSerializer.MaximumManifestBytes);
            var actualHash = GuestLaunchManifestSerializer.ComputeSha256Hex(manifestBytes);
            var expectedSidecar = Encoding.ASCII.GetBytes(
                $"{actualHash}  {GuestConfigStager.LaunchManifestFileName}\n");
            var actualSidecar = ReadBoundedFile(paths.SidecarPath, expectedSidecar.Length);

            if (actualSidecar.Length != expectedSidecar.Length ||
                !CryptographicOperations.FixedTimeEquals(actualSidecar, expectedSidecar))
            {
                return false;
            }

            var parsedManifest = GuestLaunchManifestSerializer.Deserialize(manifestBytes);
            if (File.Exists(paths.CatalogPath))
            {
                EnsurePlainFile(paths.CatalogPath);
                var catalogBytes = ReadBoundedFile(paths.CatalogPath, MaximumCatalogBytes);
                var catalogHash = GuestLaunchManifestSerializer.ComputeSha256Hex(catalogBytes);
                if (!string.Equals(
                        catalogHash,
                        parsedManifest.CatalogSha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }

            manifest = parsedManifest;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (LaunchManifestValidationException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void EnsurePlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException("The config entry must be a regular file.");
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The config entry exceeds its size limit.");
        }

        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[4096];
        while (true)
        {
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            if (output.Length + bytesRead > maximumBytes)
            {
                throw new InvalidDataException("The config entry exceeds its size limit.");
            }

            output.Write(buffer, 0, bytesRead);
        }

        return output.ToArray();
    }

    private readonly record struct ConfigPaths(
        string ManifestPath,
        string SidecarPath,
        string CatalogPath);
}
