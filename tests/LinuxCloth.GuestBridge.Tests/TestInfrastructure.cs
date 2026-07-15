using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LinuxCloth.Core;
using LinuxCloth.Wsb;

namespace LinuxCloth.GuestBridge.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"linuxcloth-guestbridge-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class FakeDriveProvider(params string[] roots) : IConfigDriveProvider
{
    public IReadOnlyList<string> GetReadyDriveRoots() => roots;
}

internal sealed class FakeBootstrapLauncher : IBootstrapLauncher
{
    private readonly int _exitCode;
    private readonly bool _throwOnLaunch;

    public FakeBootstrapLauncher(int exitCode = 0, bool throwOnLaunch = false)
    {
        _exitCode = exitCode;
        _throwOnLaunch = throwOnLaunch;
    }

    public int LaunchCount { get; private set; }

    public IReadOnlyList<ServiceId>? ServiceIds { get; private set; }

    public Task<int> LaunchAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken)
    {
        LaunchCount++;
        ServiceIds = serviceIds;
        if (_throwOnLaunch)
        {
            throw new Win32Exception("Synthetic process start failure.");
        }

        return Task.FromResult(_exitCode);
    }
}

internal sealed class CapturingProcessRunner : IProcessRunner
{
    public ProcessStartInfo? StartInfo { get; private set; }

    public Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        StartInfo = startInfo;
        return Task.FromResult(0);
    }
}

internal static class ConfigFixture
{
    private static readonly byte[] DefaultCatalog = Encoding.UTF8.GetBytes("<catalog />");

    public static GuestLaunchManifest WriteValid(
        string root,
        IReadOnlyList<ServiceId>? serviceIds = null,
        byte[]? catalogBytes = null,
        bool includeCatalog = false)
    {
        Directory.CreateDirectory(root);
        var catalog = catalogBytes ?? DefaultCatalog;
        var manifest = new GuestLaunchManifest(
            Guid.NewGuid(),
            serviceIds ?? [ServiceId.Parse("WooriBank")],
            GuestLaunchManifestSerializer.ComputeSha256Hex(catalog),
            networkEnabled: true,
            clipboardEnabled: false,
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
        var manifestBytes = GuestLaunchManifestSerializer.SerializeToUtf8Bytes(manifest);
        var manifestHash = GuestLaunchManifestSerializer.ComputeSha256Hex(manifestBytes);

        File.WriteAllBytes(
            System.IO.Path.Combine(root, GuestConfigStager.LaunchManifestFileName),
            manifestBytes);
        File.WriteAllText(
            System.IO.Path.Combine(root, GuestConfigStager.LaunchManifestHashFileName),
            $"{manifestHash}  {GuestConfigStager.LaunchManifestFileName}\n",
            Encoding.ASCII);

        if (includeCatalog)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(root, GuestConfigStager.CatalogFileName),
                catalog);
        }

        return manifest;
    }
}
