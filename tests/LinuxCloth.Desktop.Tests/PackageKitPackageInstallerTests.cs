using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class PackageKitPackageInstallerTests
{
    [Fact]
    public async Task ResolvesInstalledAndSimulatedChangesWithRepositoryAndSize()
    {
        var client = new FakePackageKitClient
        {
            Resolved =
            [
                new PackageKitPackage(1, "bubblewrap;1.0;x86_64;installed:system", "sandbox"),
                new PackageKitPackage(2, "qemu;9.0;x86_64;updates", "virtual machine"),
            ],
            Simulated =
            [
                new PackageKitPackage(27, "qemu;9.0;x86_64;updates", "virtual machine"),
                new PackageKitPackage(27, "dependency;2.0;x86_64;base", "dependency"),
            ],
            Details = new Dictionary<string, PackageKitDetails>(StringComparer.Ordinal)
            {
                ["qemu;9.0;x86_64;updates"] =
                    new("qemu;9.0;x86_64;updates", 100, "QEMU"),
                ["dependency;2.0;x86_64;base"] =
                    new("dependency;2.0;x86_64;base", 50, "Dependency"),
            },
        };
        await using var installer = new PackageKitPackageInstaller(client);

        var preview = await installer.ResolveAsync(CreatePlan("bubblewrap", "qemu"));

        Assert.True(preview.CanInstall);
        Assert.Equal(150UL, preview.DownloadSize);
        Assert.Equal(["base", "updates"], preview.Repositories);
        Assert.Contains(preview.Changes, change =>
            change.Name == "bubblewrap" && change.Kind == PackageChangeKind.AlreadyInstalled);
        Assert.Contains(preview.Changes, change =>
            change.Name == "dependency" && change.Kind == PackageChangeKind.Install);

        var result = await installer.InstallAsync(
            preview,
            new Progress<PackageInstallProgress>());

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["qemu;9.0;x86_64;updates", "dependency;2.0;x86_64;base"],
            client.InstalledPackageIds);
    }

    [Fact]
    public async Task PackageKitAbsenceProducesManualOnlyPreview()
    {
        var client = new FakePackageKitClient { IsAvailable = false };
        await using var installer = new PackageKitPackageInstaller(client);

        var preview = await installer.ResolveAsync(CreatePlan("qemu"));

        Assert.False(preview.IsPackageKitAvailable);
        Assert.False(preview.CanInstall);
        Assert.Contains("sudo apt install -- qemu", preview.Plan.ManualInstallCommand, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(
            preview,
            new Progress<PackageInstallProgress>()));
    }

    [Fact]
    public async Task UnresolvedPackagesBlockInstallation()
    {
        var client = new FakePackageKitClient { Resolved = [] };
        await using var installer = new PackageKitPackageInstaller(client);

        var preview = await installer.ResolveAsync(CreatePlan("missing"));

        Assert.Equal(["missing"], preview.UnresolvedPackages);
        Assert.False(preview.CanInstall);
    }

    [Fact]
    public async Task PreviewCannotBeReusedByAnotherInstaller()
    {
        var firstClient = CreateInstallableClient();
        var secondClient = CreateInstallableClient();
        await using var first = new PackageKitPackageInstaller(firstClient);
        await using var second = new PackageKitPackageInstaller(secondClient);
        var preview = await first.ResolveAsync(CreatePlan("qemu"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => second.InstallAsync(
            preview,
            new Progress<PackageInstallProgress>()));
    }

    private static FakePackageKitClient CreateInstallableClient() => new()
    {
        Resolved = [new PackageKitPackage(2, "qemu;9.0;x86_64;updates", "QEMU")],
        Simulated = [new PackageKitPackage(27, "qemu;9.0;x86_64;updates", "QEMU")],
        Details = new Dictionary<string, PackageKitDetails>(StringComparer.Ordinal)
        {
            ["qemu;9.0;x86_64;updates"] = new("qemu;9.0;x86_64;updates", 100, "QEMU"),
        },
    };

    private static PackagePlan CreatePlan(params string[] packages) => new(
        DistributionFamily.Debian,
        packages,
        [],
        packages,
        $"sudo apt install -- {string.Join(' ', packages)}");

    private sealed class FakePackageKitClient : IPackageKitClient
    {
        public bool IsAvailable { get; init; } = true;

        public IReadOnlyList<PackageKitPackage> Resolved { get; init; } = [];

        public IReadOnlyList<PackageKitPackage> Simulated { get; init; } = [];

        public IReadOnlyDictionary<string, PackageKitDetails> Details { get; init; } =
            new Dictionary<string, PackageKitDetails>(StringComparer.Ordinal);

        public IReadOnlyList<string> InstalledPackageIds { get; private set; } = [];

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(IsAvailable);
        }

        public Task<IReadOnlyList<PackageKitPackage>> ResolveAsync(
            IReadOnlyList<string> packageNames,
            CancellationToken cancellationToken = default)
        {
            _ = packageNames;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Resolved);
        }

        public Task<IReadOnlyList<PackageKitPackage>> SimulateInstallAsync(
            IReadOnlyList<string> packageIds,
            CancellationToken cancellationToken = default)
        {
            _ = packageIds;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Simulated);
        }

        public Task<IReadOnlyDictionary<string, PackageKitDetails>> GetDetailsAsync(
            IReadOnlyList<string> packageIds,
            CancellationToken cancellationToken = default)
        {
            _ = packageIds;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Details);
        }

        public Task InstallAsync(
            IReadOnlyList<string> packageIds,
            IProgress<PackageInstallProgress> progress,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();
            InstalledPackageIds = packageIds.ToArray();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
