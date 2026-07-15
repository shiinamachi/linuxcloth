using LinuxCloth.Application.ImageBuilding;

namespace LinuxCloth.Application.Tests.ImageBuilding;

public sealed class ImageBuilderCommandFactoryTests
{
    [Fact]
    public async Task CreatesSparseBaseDirectlyAtTheManagedStagingPath()
    {
        using var fixture = new ImageBuildFixture();

        var workspace = await fixture.BeginAsync();

        var command = Assert.Single(fixture.Runner.Specs);
        Assert.Equal(fixture.Toolchain.Bubblewrap, command.FileName);
        Assert.Equal(fixture.Toolchain.QemuImg, command.IdentityExecutablePath);
        var separator = command.Arguments.ToList().IndexOf("--");
        var qemuImgArguments = command.Arguments.Skip(separator + 2).ToArray();
        Assert.Equal(workspace.Staging.BaseImagePath, qemuImgArguments[^2]);
        Assert.Equal("96G", qemuImgArguments[^1]);
        Assert.Contains(
            qemuImgArguments,
            argument => argument.Contains("preallocation=metadata", StringComparison.Ordinal));
        Assert.Equal(workspace.Staging.DirectoryPath, command.WorkingDirectory);
        Assert.Contains("--unshare-all", command.Arguments);
        Assert.DoesNotContain("--share-net", command.Arguments);
        Assert.True(File.Exists(workspace.Staging.BaseImagePath));
        Assert.False(File.Exists(Path.Combine(workspace.Staging.DirectoryPath, "Windows 11; $(not-a-shell).iso")));
    }

    [Fact]
    public async Task KeepsHostileLookingMediaPathsAsSingleArguments()
    {
        using var fixture = new ImageBuildFixture();
        var workspace = await fixture.BeginAsync();

        var qemu = ImageBuilderCommandFactory.BuildQemu(workspace);

        Assert.Contains(
            qemu.Arguments,
            argument => argument.Contains("$(not-a-shell)", StringComparison.Ordinal));
        Assert.DoesNotContain("sh", qemu.Arguments);
        Assert.DoesNotContain("-c", qemu.Arguments);
        Assert.Contains(
            qemu.Arguments,
            argument => argument.StartsWith("if=none,id=windows-install", StringComparison.Ordinal) &&
                        argument.Contains("readonly=on", StringComparison.Ordinal));
        Assert.Contains(
            qemu.Arguments,
            argument => argument.StartsWith("if=none,id=linuxcloth-provisioning", StringComparison.Ordinal) &&
                        argument.Contains("readonly=on", StringComparison.Ordinal) &&
                        argument.Contains(workspace.ProvisioningIsoPath, StringComparison.Ordinal));
        Assert.DoesNotContain(qemu.Arguments, argument => argument.Contains("-net", StringComparison.Ordinal));
        Assert.Contains(
            qemu.Arguments,
            argument => argument.Contains("disable-copy-paste=on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerificationBootHasNoInstallationMediaOrNetworkAndOnlyWritableProbeChannel()
    {
        using var fixture = new ImageBuildFixture();
        var workspace = await fixture.BeginAsync();

        var qemu = ImageBuilderCommandFactory.BuildVerificationQemu(workspace);

        Assert.DoesNotContain(qemu.Arguments, argument => argument.Contains("windows-install", StringComparison.Ordinal));
        Assert.DoesNotContain(qemu.Arguments, argument => argument.Contains("virtio-drivers", StringComparison.Ordinal));
        Assert.DoesNotContain(qemu.Arguments, argument => argument.Contains("linuxcloth-provisioning", StringComparison.Ordinal));
        Assert.DoesNotContain(qemu.Arguments, argument => argument.Contains("-net", StringComparison.Ordinal));
        Assert.Contains(
            qemu.Arguments,
            argument => argument.Contains("id=linuxcloth-verification", StringComparison.Ordinal) &&
                        argument.Contains("fat:rw:", StringComparison.Ordinal));
        var confined = ImageBuilderCommandFactory.ConfineQemu(workspace, qemu);
        Assert.DoesNotContain(fixture.WindowsIsoPath, confined.Arguments);
        Assert.DoesNotContain(fixture.VirtioWinIsoPath, confined.Arguments);
        Assert.DoesNotContain(WindowsImageBuildStateStore.GetManifestPath(workspace.Staging), confined.Arguments);
    }

    [Fact]
    public async Task BubblewrapMountsOnlyMediaFirmwareAndOwnedWritableDirectories()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageBuildFixture();
        var workspace = await fixture.BeginAsync();
        var qemu = ImageBuilderCommandFactory.BuildQemu(workspace);

        var confined = ImageBuilderCommandFactory.ConfineQemu(workspace, qemu);

        Assert.Equal(fixture.Toolchain.Bubblewrap, confined.FileName);
        AssertContainsSequence(confined.Arguments, "--unshare-all", "--clearenv");
        Assert.DoesNotContain("--share-net", confined.Arguments);
        AssertContainsSequence(
            confined.Arguments,
            "--ro-bind",
            fixture.WindowsIsoPath,
            fixture.WindowsIsoPath);
        AssertContainsSequence(
            confined.Arguments,
            "--ro-bind",
            fixture.VirtioWinIsoPath,
            fixture.VirtioWinIsoPath);
        AssertContainsSequence(
            confined.Arguments,
            "--bind",
            workspace.Staging.BaseImagePath,
            workspace.Staging.BaseImagePath);
        AssertContainsSequence(
            confined.Arguments,
            "--bind",
            workspace.Staging.OvmfVariablesTemplatePath,
            workspace.Staging.OvmfVariablesTemplatePath);
        AssertContainsSequence(
            confined.Arguments,
            "--bind",
            workspace.SocketsDirectory,
            workspace.SocketsDirectory);
        Assert.DoesNotContain(WindowsImageBuildStateStore.GetManifestPath(workspace.Staging), confined.Arguments);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), confined.Arguments);
        var separator = confined.Arguments.ToList().IndexOf("--");
        Assert.True(separator >= 0);
        Assert.Equal(qemu.FileName, confined.Arguments[separator + 1]);
        Assert.Equal(qemu.Arguments, confined.Arguments.Skip(separator + 2));
    }

    private static void AssertContainsSequence(IReadOnlyList<string> values, params string[] expected)
    {
        for (var index = 0; index <= values.Count - expected.Length; index++)
        {
            if (values.Skip(index).Take(expected.Length).SequenceEqual(expected))
            {
                return;
            }
        }

        Assert.Fail($"Expected sequence not found: {string.Join(" | ", expected)}");
    }
}
