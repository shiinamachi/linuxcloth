using LinuxCloth.Catalog;

namespace LinuxCloth.Cli.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void EmptyArgumentsShowHelp()
    {
        var result = CliParser.Parse([]);

        Assert.True(result.IsSuccess);
        Assert.IsType<HelpCommand>(result.Command);
    }

    [Fact]
    public void RunUsesSecureDefaults()
    {
        var result = CliParser.Parse(
            ["run", "WooriBank", "--image", "windows-11"]);

        var command = Assert.IsType<RunCommand>(result.Command);
        Assert.Null(result.Error);
        Assert.Equal("windows-11", command.ImageId.Value);
        Assert.Equal(["WooriBank"], command.ServiceIds.Select(static id => id.Value));
        Assert.Equal(4, command.CpuCount);
        Assert.Equal(6144, command.MemoryMiB);
        Assert.True(command.NetworkEnabled);
        Assert.False(command.ClipboardEnabled);
        Assert.Null(command.CatalogRoot);
    }

    [Fact]
    public void RunParsesExplicitResourceAndIsolationOptions()
    {
        var result = CliParser.Parse(
        [
            "run",
            "WooriBank",
            "Government24",
            "--image", "win11-pro",
            "--cpus", "8",
            "--memory-mib", "8192",
            "--no-network",
            "--enable-clipboard",
            "--catalog-root", "/opt/linuxcloth/catalog",
        ]);

        var command = Assert.IsType<RunCommand>(result.Command);
        Assert.Equal(8, command.CpuCount);
        Assert.Equal(8192, command.MemoryMiB);
        Assert.False(command.NetworkEnabled);
        Assert.True(command.ClipboardEnabled);
        Assert.Equal("/opt/linuxcloth/catalog", command.CatalogRoot);
        Assert.Equal(2, command.ServiceIds.Count);
    }

    [Theory]
    [InlineData("run", "WooriBank")]
    [InlineData("run", "WooriBank", "--image", "Windows11")]
    [InlineData("run", "WooriBank", "--image", "win11", "--image", "win12")]
    [InlineData("run", "WooriBank", "--image", "win11", "--unknown")]
    [InlineData("run", "WooriBank", "WooriBank", "--image", "win11")]
    public void RunRejectsInvalidInput(params string[] arguments)
    {
        var result = CliParser.Parse(arguments);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void CatalogSearchParsesCategoryAndRoot()
    {
        var result = CliParser.Parse(
        [
            "catalog", "search", "우리은행",
            "--category", "banking",
            "--catalog-root", "/catalog",
        ]);

        var command = Assert.IsType<CatalogSearchCommand>(result.Command);
        Assert.Equal("우리은행", command.Query);
        Assert.Equal(CatalogCategory.Banking, command.Category);
        Assert.Equal("/catalog", command.CatalogRoot);
    }

    [Fact]
    public void SearchRequiresExactlyOneQuery()
    {
        var missing = CliParser.Parse(["catalog", "search"]);
        var extra = CliParser.Parse(["catalog", "search", "one", "two"]);

        Assert.False(missing.IsSuccess);
        Assert.False(extra.IsSuccess);
    }

    [Fact]
    public void HelpCanTargetSubcommand()
    {
        var result = CliParser.Parse(["catalog", "search", "--help"]);

        var command = Assert.IsType<HelpCommand>(result.Command);
        Assert.Equal("catalog search", command.Topic);
    }

    [Fact]
    public void ImageBuildStartParsesAbsoluteMediaAndResources()
    {
        var result = CliParser.Parse(
        [
            "image", "build", "start", "win11-pro",
            "--windows-iso", "/media/windows.iso",
            "--virtio-win-iso", "/media/virtio.iso",
            "--guest-bridge", "/opt/linuxcloth/linuxcloth-guest-bridge.exe",
            "--disk-gib", "128",
            "--cpus", "8",
            "--memory-mib", "12288",
            "--windows-image-index", "6",
        ]);

        var command = Assert.IsType<ImageBuildStartCommand>(result.Command);
        Assert.Equal("win11-pro", command.ImageId.Value);
        Assert.Equal("/media/windows.iso", command.WindowsIsoPath);
        Assert.Equal("/media/virtio.iso", command.VirtioWinIsoPath);
        Assert.Equal(
            "/opt/linuxcloth/linuxcloth-guest-bridge.exe",
            command.GuestBridgeExecutablePath);
        Assert.Equal(128, command.DiskSizeGiB);
        Assert.Equal(8, command.CpuCount);
        Assert.Equal(12288, command.MemoryMiB);
        Assert.Equal(6, command.WindowsImageIndex);
    }

    [Fact]
    public void ImageBuildStartRejectsRelativeOrMissingMedia()
    {
        var relative = CliParser.Parse(
        [
            "image", "build", "start", "win11",
            "--windows-iso", "windows.iso",
            "--virtio-win-iso", "/media/virtio.iso",
            "--guest-bridge", "/opt/linuxcloth/linuxcloth-guest-bridge.exe",
        ]);
        var missing = CliParser.Parse(
        [
            "image", "build", "start", "win11",
            "--windows-iso", "/media/windows.iso",
            "--virtio-win-iso", "/media/virtio.iso",
        ]);

        Assert.False(relative.IsSuccess);
        Assert.False(missing.IsSuccess);
    }

    [Fact]
    public void ImageBuildResumeParsesPreservedStaging()
    {
        var result = CliParser.Parse(
        [
            "image", "build", "resume", "win11",
            "--staging", "/data/images/.staging-win11-abc",
        ]);

        var command = Assert.IsType<ImageBuildResumeCommand>(result.Command);
        Assert.Equal("/data/images/.staging-win11-abc", command.StagingDirectory);
    }

    [Fact]
    public void ImageBuildRecoverUsesPersistedProcessIdentity()
    {
        var result = CliParser.Parse(
        [
            "image", "build", "recover", "win11",
            "--staging", "/data/images/.staging-win11-abc",
        ]);
        var obsoleteReset = CliParser.Parse(
            ["image", "build", "reset", "win11", "--staging", "/tmp/staging"]);

        var command = Assert.IsType<ImageBuildRecoverCommand>(result.Command);
        Assert.Equal("/data/images/.staging-win11-abc", command.StagingDirectory);
        Assert.False(obsoleteReset.IsSuccess);
    }
}
