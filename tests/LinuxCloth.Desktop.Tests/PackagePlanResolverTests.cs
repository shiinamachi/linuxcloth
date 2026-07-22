using LinuxCloth.Desktop.Setup;

namespace LinuxCloth.Desktop.Tests;

public sealed class PackagePlanResolverTests
{
    [Theory]
    [InlineData(DistributionFamily.Arch, "sudo pacman -S --needed --")]
    [InlineData(DistributionFamily.Debian, "sudo apt install --")]
    [InlineData(DistributionFamily.Fedora, "sudo dnf install --")]
    public async Task BuildsDeterministicPlanFromPackagingManifests(
        DistributionFamily family,
        string commandPrefix)
    {
        var source = new FakeManifestSource(
            "qemu-system-x86\nbubblewrap\n",
            "7zip\nxorriso\nbubblewrap\n");
        var resolver = new PackagePlanResolver(source);
        var distribution = new DistributionInfo(
            family switch
            {
                DistributionFamily.Arch => "arch",
                DistributionFamily.Debian => "debian",
                _ => "fedora",
            },
            null,
            null,
            [],
            family,
            "/etc/os-release");

        var plan = await resolver.ResolveAsync(distribution);

        Assert.Equal(["qemu-system-x86", "bubblewrap"], plan.RuntimePackages);
        Assert.Equal(["7zip", "xorriso", "bubblewrap"], plan.ImageBuildPackages);
        Assert.Equal(["qemu-system-x86", "bubblewrap", "7zip", "xorriso"], plan.AllPackages);
        Assert.StartsWith(commandPrefix, plan.ManualInstallCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnsupportedDistributions()
    {
        var resolver = new PackagePlanResolver(new FakeManifestSource(string.Empty, string.Empty));
        var distribution = new DistributionInfo(
            "opensuse",
            "openSUSE",
            null,
            [],
            DistributionFamily.Unsupported,
            "/etc/os-release");

        await Assert.ThrowsAsync<NotSupportedException>(() => resolver.ResolveAsync(distribution));
    }

    [Fact]
    public async Task PackagedArchHintsIncludeSecureBootEnrollmentCapability()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resolver = new PackagePlanResolver(
            new FilePackageManifestSource(Path.Combine(repositoryRoot, "packaging")));
        var distribution = new DistributionInfo(
            "arch",
            "Arch Linux",
            null,
            [],
            DistributionFamily.Arch,
            "/etc/os-release");

        var plan = await resolver.ResolveAsync(distribution);

        Assert.Contains("edk2-ovmf", plan.RuntimePackages);
        Assert.Contains("virt-firmware", plan.ImageBuildPackages);
        Assert.StartsWith(
            "sudo pacman -S --needed -- ",
            plan.ManualInstallCommand,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid-package\n")]
    [InlineData("# comment\nvalid-package\n\n")]
    public void AcceptsSafePackageManifestEntries(string contents)
    {
        Assert.Equal(["valid-package"], PackagePlanResolver.ParseManifest(contents));
    }

    [Theory]
    [InlineData("package name")]
    [InlineData("package;command")]
    [InlineData("--option")]
    public void RejectsUnsafePackageManifestEntries(string contents)
    {
        Assert.Throws<InvalidDataException>(() => PackagePlanResolver.ParseManifest(contents));
    }

    private sealed class FakeManifestSource(string runtime, string imageBuild) : IPackageManifestSource
    {
        public Task<string> ReadAsync(
            DistributionFamily family,
            bool imageBuildManifest,
            CancellationToken cancellationToken = default)
        {
            _ = family;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(imageBuildManifest ? imageBuild : runtime);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("linuxcloth 저장소 루트를 찾지 못했습니다.");
    }
}
