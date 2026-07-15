using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LinuxCloth.Application.Images;

namespace LinuxCloth.Application.Tests.Images;

public sealed class ManagedImageRegistryTests
{
    [Fact]
    public void CreatesPrivateInRegistryStagingWithFixedArtifactPaths()
    {
        using var fixture = new ImageRegistryFixture();

        var staging = fixture.Registry.CreateStaging(ImageId.Parse("windows-11"));

        Assert.Equal(fixture.Paths.ImagesDirectory, Path.GetDirectoryName(staging.DirectoryPath));
        Assert.Equal("base.qcow2", Path.GetFileName(staging.BaseImagePath));
        Assert.Equal("ovmf-vars.template.fd", Path.GetFileName(staging.OvmfVariablesTemplatePath));
        Assert.Equal("swtpm-state.template", Path.GetFileName(staging.SwtpmStateTemplateDirectory));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(staging.DirectoryPath));
        }
    }

    [Fact]
    public async Task PromotesByMovingStagingAndReturnsSessionDefinition()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");
        var machineId = Guid.NewGuid();
        var logicalLength = new FileInfo(staging.BaseImagePath).Length;

        var image = await fixture.Registry.PromoteAsync(
            staging,
            machineId,
            fixture.OvmfCodePath);

        Assert.False(Directory.Exists(staging.DirectoryPath));
        Assert.Equal(logicalLength, new FileInfo(image.BaseImagePath).Length);
        Assert.Equal(
            Path.Combine(fixture.Paths.ImagesDirectory, "windows-11", "base.qcow2"),
            image.BaseImagePath);
        var sessionImage = image.ToSessionImageDefinition();
        Assert.Equal("windows-11", sessionImage.ImageId);
        Assert.Equal(machineId, sessionImage.MachineId);
        Assert.Equal(Path.GetFullPath(fixture.OvmfCodePath), sessionImage.OvmfCodePath);
    }

    [Fact]
    public async Task SealsManagedAssetsAndVerifiesTheirHashes()
    {
        using var fixture = new ImageRegistryFixture();

        var image = await fixture.PromoteReadyAsync("windows-11");
        var verification = await fixture.Registry.VerifyAsync(image.ImageId);

        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Issues));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(image.BaseImagePath));
            Assert.Equal(
                UnixFileMode.UserRead,
                File.GetUnixFileMode(Path.Combine(image.DirectoryPath, "metadata.json")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(image.DirectoryPath));
        }
    }

    [Fact]
    public async Task LoadReturnsPersistedMetadataWithoutRewritingAssets()
    {
        using var fixture = new ImageRegistryFixture();
        var promoted = await fixture.PromoteReadyAsync("windows-11");
        var metadataMtime = File.GetLastWriteTimeUtc(Path.Combine(promoted.DirectoryPath, "metadata.json"));

        var loaded = await fixture.Registry.LoadAsync(ImageId.Parse("windows-11"));

        Assert.Equal(promoted.Metadata, loaded.Metadata);
        Assert.Equal(metadataMtime, File.GetLastWriteTimeUtc(Path.Combine(loaded.DirectoryPath, "metadata.json")));
    }

    [Fact]
    public async Task LoadsSealedVersionOneMetadataWithoutBuildProvenance()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("legacy-windows-11");
        RewriteMetadata(
            image,
            json =>
            {
                var root = JsonNode.Parse(json)!.AsObject();
                root["schemaVersion"] = 1;
                Assert.True(root.Remove("buildProvenance"));
                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            });

        var loaded = await fixture.Registry.LoadAsync(image.ImageId);

        Assert.Equal(1, loaded.Metadata.SchemaVersion);
        Assert.Null(loaded.Metadata.BuildProvenance);
    }

    [Fact]
    public async Task ListsImagesInIdentifierOrderAndIgnoresPreservedStaging()
    {
        using var fixture = new ImageRegistryFixture();
        var unfinished = fixture.Registry.CreateStaging(ImageId.Parse("unfinished"));
        _ = await fixture.PromoteReadyAsync("zulu");
        _ = await fixture.PromoteReadyAsync("alpha");

        var images = await fixture.Registry.ListAsync();
        var staging = fixture.Registry.ListStaging();

        Assert.Equal(["alpha", "zulu"], images.Select(image => image.ImageId.Value));
        Assert.Equal([unfinished], staging);
    }

    [Fact]
    public async Task ListRejectsAnEntryThatOnlyPretendsToBeStaging()
    {
        using var fixture = new ImageRegistryFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Paths.ImagesDirectory, ".staging-untrusted"));

        await Assert.ThrowsAsync<ImageRegistryException>(() => fixture.Registry.ListAsync());
    }

    [Fact]
    public async Task AConcurrentPromotionNeverOverwritesAnExistingImage()
    {
        using var fixture = new ImageRegistryFixture();
        var first = fixture.CreateReadyStaging("windows-11");
        var second = fixture.CreateReadyStaging("windows-11");
        var firstImage = await fixture.Registry.PromoteAsync(first, Guid.NewGuid(), fixture.OvmfCodePath);
        var firstHash = firstImage.Metadata.BaseImage.Sha256;

        await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(second, Guid.NewGuid(), fixture.OvmfCodePath));

        Assert.True(Directory.Exists(second.DirectoryPath));
        var loaded = await fixture.Registry.LoadAsync(ImageId.Parse("windows-11"));
        Assert.Equal(firstHash, loaded.Metadata.BaseImage.Sha256);
    }

    [Fact]
    public async Task PromotionFailurePreservesStagingByDefault()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.Registry.CreateStaging(ImageId.Parse("windows-11"));
        File.WriteAllText(staging.OvmfVariablesTemplatePath, "vars");

        await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(staging, Guid.NewGuid(), fixture.OvmfCodePath));

        Assert.True(Directory.Exists(staging.DirectoryPath));
        Assert.Equal(
            staging,
            fixture.Registry.OpenStaging(staging.ImageId, staging.DirectoryPath));
    }

    [Fact]
    public async Task ARecoveredStagingAreaDiscardsOnlyItsOwnStaleMetadataFiles()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");
        File.WriteAllText(Path.Combine(staging.DirectoryPath, "metadata.json"), "stale");
        File.WriteAllText(
            Path.Combine(staging.DirectoryPath, ".metadata.tmp-0123456789abcdef0123456789abcdef"),
            "partial");
        var reopened = fixture.Registry.OpenStaging(staging.ImageId, staging.DirectoryPath);

        var image = await fixture.Registry.PromoteAsync(
            reopened,
            Guid.NewGuid(),
            fixture.OvmfCodePath);

        Assert.Equal("windows-11", image.ImageId.Value);
        Assert.False(File.Exists(Path.Combine(image.DirectoryPath, ".metadata.tmp-0123456789abcdef0123456789abcdef")));
    }

    [Fact]
    public async Task ExplicitDeleteBehaviorRemovesFailedStaging()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.Registry.CreateStaging(ImageId.Parse("windows-11"));

        await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(
                staging,
                Guid.NewGuid(),
                fixture.OvmfCodePath,
                RegistrationFailureBehavior.DeleteStaging));

        Assert.False(Directory.Exists(staging.DirectoryPath));
    }

    [Fact]
    public async Task ExplicitDeleteBehaviorAlsoAppliesToInvalidPromotionArguments()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Registry.PromoteAsync(
                staging,
                Guid.Empty,
                fixture.OvmfCodePath,
                RegistrationFailureBehavior.DeleteStaging));

        Assert.False(Directory.Exists(staging.DirectoryPath));
    }

    [Theory]
    [InlineData(RegistrationFailureBehavior.PreserveStaging, true)]
    [InlineData(RegistrationFailureBehavior.DeleteStaging, false)]
    public async Task CancellationHonorsExplicitStagingBehavior(
        RegistrationFailureBehavior behavior,
        bool expectedToRemain)
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Registry.PromoteAsync(
                staging,
                Guid.NewGuid(),
                fixture.OvmfCodePath,
                behavior,
                cancellation.Token));

        Assert.Equal(expectedToRemain, Directory.Exists(staging.DirectoryPath));
    }

    [Fact]
    public void AbandonRejectsAStagingPathOutsideTheRegistry()
    {
        using var fixture = new ImageRegistryFixture();
        var outside = Path.Combine(fixture.Root, ".staging-windows-11-0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(outside);

        Assert.Throws<ImageRegistryException>(() =>
            fixture.Registry.OpenStaging(ImageId.Parse("windows-11"), outside));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task RejectsUnknownStagingContent()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");
        File.WriteAllText(Path.Combine(staging.DirectoryPath, "installer.iso"), "not-managed");

        var exception = await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(staging, Guid.NewGuid(), fixture.OvmfCodePath));

        Assert.Contains("unknown entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAReparsePointInTheTpmTreeWithoutDeletingItsTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11", []);
        var outside = Path.Combine(fixture.Root, "outside-tpm");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateSymbolicLink(
            Path.Combine(staging.SwtpmStateTemplateDirectory, "linked"),
            outside);

        await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(
                staging,
                Guid.NewGuid(),
                fixture.OvmfCodePath,
                RegistrationFailureBehavior.DeleteStaging));

        Assert.False(Directory.Exists(staging.DirectoryPath));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task RejectsAnOvmfPathThatTraversesASymbolicLink()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11");
        var firmwareLink = Path.Combine(fixture.Root, "firmware-link");
        Directory.CreateSymbolicLink(firmwareLink, Path.GetDirectoryName(fixture.OvmfCodePath)!);
        var linkedCode = Path.Combine(firmwareLink, Path.GetFileName(fixture.OvmfCodePath));

        await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(staging, Guid.NewGuid(), linkedCode));
    }

    [Fact]
    public async Task EnforcesTheTpmFileCountLimit()
    {
        using var fixture = new ImageRegistryFixture();
        var files = Enumerable.Range(0, ImageRegistryLimits.MaximumTpmFileCount + 1)
            .Select(index => ($"state-{index:D3}", index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var staging = fixture.CreateReadyStaging("windows-11", files);

        var exception = await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(staging, Guid.NewGuid(), fixture.OvmfCodePath));

        Assert.Contains("file or byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnforcesTheTpmByteLimitBeforeReadingOversizedState()
    {
        using var fixture = new ImageRegistryFixture();
        var staging = fixture.CreateReadyStaging("windows-11", []);
        var oversized = Path.Combine(staging.SwtpmStateTemplateDirectory, "oversized-state");
        using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(ImageRegistryLimits.MaximumTpmTotalBytes + 1);
        }

        var exception = await Assert.ThrowsAsync<ImageRegistryException>(() =>
            fixture.Registry.PromoteAsync(staging, Guid.NewGuid(), fixture.OvmfCodePath));

        Assert.Contains("file or byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TpmTreeHashIsStableForTheSameOrderedState()
    {
        using var fixture = new ImageRegistryFixture();
        var first = fixture.CreateReadyStaging(
            "first",
            [("b/state", "two"), ("a/state", "one")]);
        var second = fixture.CreateReadyStaging(
            "second",
            [("a/state", "one"), ("b/state", "two")]);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SetTreeTimestamps(first.SwtpmStateTemplateDirectory, timestamp);
        SetTreeTimestamps(second.SwtpmStateTemplateDirectory, timestamp);

        var firstImage = await fixture.Registry.PromoteAsync(first, Guid.NewGuid(), fixture.OvmfCodePath);
        var secondImage = await fixture.Registry.PromoteAsync(second, Guid.NewGuid(), fixture.OvmfCodePath);

        Assert.Equal(
            firstImage.Metadata.SwtpmStateTemplate.Sha256,
            secondImage.Metadata.SwtpmStateTemplate.Sha256);
    }

    [Fact]
    public async Task VerifyReportsBaseImageContentAndModeTampering()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("windows-11");
        MakeFileWritable(image.BaseImagePath);
        await using (var stream = new FileStream(image.BaseImagePath, FileMode.Open, FileAccess.Write))
        {
            await stream.WriteAsync("changed"u8.ToArray());
        }

        var result = await fixture.Registry.VerifyAsync(image.ImageId);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue =>
            issue.Artifact == "baseImage" && issue.Code == "hash-mismatch");
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains(result.Issues, issue => issue.Code == "mode-mismatch");
        }
    }

    [Fact]
    public async Task VerifyReportsExternalFirmwareTampering()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("windows-11");
        File.WriteAllText(fixture.OvmfCodePath, "replaced-firmware");

        var result = await fixture.Registry.VerifyAsync(image.ImageId);

        Assert.Contains(result.Issues, issue =>
            issue.Artifact == "ovmfCode" && issue.Code == "hash-mismatch");
    }

    [Fact]
    public async Task VerifyReportsTpmStateTampering()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("windows-11");
        var statePath = Directory.GetFiles(image.SwtpmStateTemplateDirectory).Single();
        MakeFileWritable(statePath);
        File.WriteAllText(statePath, "tampered-state");

        var result = await fixture.Registry.VerifyAsync(image.ImageId);

        Assert.Contains(result.Issues, issue =>
            issue.Artifact == "swtpmStateTemplate" && issue.Code == "hash-mismatch");
    }

    [Fact]
    public async Task VerifyReportsModificationTimeTamperingWithoutContentChanges()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("windows-11");
        File.SetLastWriteTimeUtc(
            image.BaseImagePath,
            File.GetLastWriteTimeUtc(image.BaseImagePath).AddSeconds(2));

        var result = await fixture.Registry.VerifyAsync(image.ImageId);

        Assert.Contains(result.Issues, issue =>
            issue.Artifact == "baseImage" && issue.Code == "mtime-mismatch");
    }

    [Fact]
    public async Task LoadRejectsUnknownAndDuplicateMetadataProperties()
    {
        using var fixture = new ImageRegistryFixture();
        var unknown = await fixture.PromoteReadyAsync("unknown-property");
        RewriteMetadata(
            unknown,
            json => json.Replace("{", "{\n  \"unexpected\": true,", StringComparison.Ordinal));

        await Assert.ThrowsAsync<ImageMetadataValidationException>(() =>
            fixture.Registry.LoadAsync(unknown.ImageId));

        var duplicate = await fixture.PromoteReadyAsync("duplicate-property");
        RewriteMetadata(
            duplicate,
            json => json.Replace(
                "\"schemaVersion\": 2,",
                "\"schemaVersion\": 2,\n  \"schemaVersion\": 2,",
                StringComparison.Ordinal));

        await Assert.ThrowsAsync<ImageMetadataValidationException>(() =>
            fixture.Registry.LoadAsync(duplicate.ImageId));
    }

    [Fact]
    public async Task LoadRejectsOversizedMetadataBeforeParsing()
    {
        using var fixture = new ImageRegistryFixture();
        var image = await fixture.PromoteReadyAsync("windows-11");
        var metadataPath = Path.Combine(image.DirectoryPath, "metadata.json");
        MakeFileWritable(metadataPath);
        await File.WriteAllBytesAsync(
            metadataPath,
            new byte[ImageRegistryLimits.MaximumMetadataBytes + 1]);

        await Assert.ThrowsAsync<ImageMetadataValidationException>(() =>
            fixture.Registry.LoadAsync(image.ImageId));
    }

    private static void RewriteMetadata(ManagedWindowsImage image, Func<string, string> transform)
    {
        var path = Path.Combine(image.DirectoryPath, "metadata.json");
        MakeFileWritable(path);
        var json = File.ReadAllText(path, Encoding.UTF8);
        File.WriteAllText(path, transform(json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void MakeFileWritable(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void SetTreeTimestamps(string root, DateTime timestamp)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, timestamp);
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Count(character => character == Path.DirectorySeparatorChar)))
        {
            Directory.SetLastWriteTimeUtc(directory, timestamp);
        }

        Directory.SetLastWriteTimeUtc(root, timestamp);
    }
}
