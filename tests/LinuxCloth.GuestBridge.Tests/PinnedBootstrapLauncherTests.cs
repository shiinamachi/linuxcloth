using System.Net;
using System.Security.Cryptography;
using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge.Tests;

public sealed class PinnedBootstrapLauncherTests
{
    private static readonly Uri TestArtifactUri = new(
        "https://github.com/yourtablecloth/TableCloth/releases/download/test/bootstrap.exe");

    [Fact]
    public void PinsImmutableOfficialReleaseArtifacts()
    {
        Assert.Equal(
            "https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/" +
            "SporkBootstrap_1.20.5.0_Release_x64.exe",
            PinnedSporkRelease.BootstrapUrl);
        Assert.Equal(5_185_888, PinnedSporkRelease.BootstrapSizeBytes);
        Assert.Equal(
            "AD953BBBECE1D2E72898164DA2E5D152A15D2E1EBBAF330A089AA1E8775CC498",
            PinnedSporkRelease.BootstrapSha256);
        Assert.Equal(
            "892C4996A8E6AD504275B228C04269B708D98455BBBB86202BEF073E9A8D320A",
            PinnedSporkRelease.BootstrapSignerCertificateSha256);
        Assert.Equal(
            "https://github.com/yourtablecloth/TableCloth/releases/download/v1.20.5/" +
            "Spork_1.20.5.0_Release_{arch}_Portable.zip",
            PinnedSporkRelease.SporkZipUrlTemplate);
        Assert.Equal(
            "x64=F8E8FE7DFDCCB7CFFD971CF153C5C83C848A6B1ECC39F13BDE5895702CF156AF;" +
            "arm64=D61B2BF93D11711E592C4ADF7528C0CE4D690A4F561783E26884577F98C60351",
            PinnedSporkRelease.SporkSha256Map);
    }

    [Fact]
    public async Task StartsVerifiedBootstrapWithOnlyPinnedArgumentArray()
    {
        var downloader = new FakeBootstrapArtifactDownloader();
        var signatureVerifier = new FakeExecutableSignatureVerifier(
            ExecutableSignatureVerificationResult.Trusted);
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(downloader, signatureVerifier, processRunner);
        var serviceIds = new[]
        {
            ServiceId.Parse("WooriBank"),
            ServiceId.Parse("KB-Bank"),
        };

        var exitCode = await launcher.LaunchAsync(serviceIds, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, downloader.DownloadCount);
        Assert.Equal(1, signatureVerifier.VerifyCount);
        Assert.Equal(
            PinnedSporkRelease.BootstrapSignerCertificateSha256,
            signatureVerifier.ExpectedSignerCertificateSha256);
        var startInfo = Assert.IsType<System.Diagnostics.ProcessStartInfo>(processRunner.StartInfo);
        Assert.Equal("SporkBootstrap.exe", Path.GetFileName(startInfo.FileName));
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.CreateNoWindow);
        Assert.Equal(
            [
                "--zip-url-template",
                PinnedSporkRelease.SporkZipUrlTemplate,
                "--sha256-map",
                PinnedSporkRelease.SporkSha256Map,
                "--site-ids",
                "WooriBank KB-Bank",
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("latest", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument.Contains("powershell", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(startInfo.WorkingDirectory));
    }

    [Fact]
    public async Task RejectsOversizedDownloadBeforeStartingBootstrap()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var httpClient = CreateHttpClient(
            _ => CreateContentResponse(body, declaredContentLength: 3));
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            expectedSizeBytes: 3,
            expectedSha256: Convert.ToHexStringLower(SHA256.HashData(body.AsSpan(0, 3))));
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Fact]
    public async Task RejectsTruncatedDownloadBeforeStartingBootstrap()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var httpClient = CreateHttpClient(
            _ => CreateContentResponse(body, declaredContentLength: 5));
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            expectedSizeBytes: 5,
            expectedSha256: new string('0', 64));
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Fact]
    public async Task RejectsHashMismatchBeforeStartingBootstrap()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var httpClient = CreateHttpClient(_ => CreateContentResponse(body));
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            body.Length,
            new string('0', 64));
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Theory]
    [InlineData(ExecutableSignatureVerificationResult.InvalidSignature)]
    [InlineData(ExecutableSignatureVerificationResult.SignerMismatch)]
    internal async Task RejectsUntrustedSignatureBeforeStartingBootstrap(
        ExecutableSignatureVerificationResult verificationResult)
    {
        var downloader = new FakeBootstrapArtifactDownloader();
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(verificationResult),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
        var destinationPath = Assert.IsType<string>(downloader.DestinationPath);
        Assert.False(File.Exists(destinationPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destinationPath)));
    }

    [Fact]
    public async Task RejectsUnexpectedRedirectHostBeforeStartingBootstrap()
    {
        using var httpClient = CreateHttpClient(
            _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://example.com/bootstrap.exe") },
            });
        var processRunner = new CapturingProcessRunner();
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            expectedSizeBytes: 4,
            expectedSha256: new string('0', 64));
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/asset/bootstrap.exe")]
    [InlineData("https://github.com/yourtablecloth/TableCloth/releases/download/test/other.exe")]
    [InlineData("https://release-assets.githubusercontent.com:444/asset/bootstrap.exe")]
    public async Task RejectsUnsafeRedirectTargetsBeforeStartingBootstrap(string location)
    {
        using var httpClient = CreateHttpClient(
            _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(location) },
            });
        var processRunner = new CapturingProcessRunner();
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            expectedSizeBytes: 4,
            expectedSha256: new string('0', 64));
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Fact]
    public async Task RejectsPartialContentEvenWhenItsBytesMatchThePinnedArtifact()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        using var httpClient = CreateHttpClient(
            _ => CreateContentResponse(body, statusCode: HttpStatusCode.PartialContent));
        var processRunner = new CapturingProcessRunner();
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            body.Length,
            Convert.ToHexString(SHA256.HashData(body)));
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
    }

    [Fact]
    public async Task FollowsOfficialReleaseAssetRedirectAndValidatesStream()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var requestCount = 0;
        using var httpClient = CreateHttpClient(
            request =>
            {
                requestCount++;
                if (requestCount == 1)
                {
                    Assert.Equal("github.com", request.RequestUri?.Host);
                    return new HttpResponseMessage(HttpStatusCode.Found)
                    {
                        Headers =
                        {
                            Location = new Uri(
                                "https://release-assets.githubusercontent.com/asset/bootstrap.exe"),
                        },
                    };
                }

                Assert.Equal("release-assets.githubusercontent.com", request.RequestUri?.Host);
                return CreateContentResponse(body);
            });
        var downloader = new HttpBootstrapArtifactDownloader(
            httpClient,
            TestArtifactUri,
            body.Length,
            Convert.ToHexStringLower(SHA256.HashData(body)));
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        var exitCode = await launcher.LaunchAsync(
            [ServiceId.Parse("WooriBank")],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, requestCount);
        Assert.Equal(1, processRunner.RunCount);
    }

    [Fact]
    public async Task RejectsDuplicateServiceIdentifiersBeforeDownloading()
    {
        var downloader = new FakeBootstrapArtifactDownloader();
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);
        var serviceId = ServiceId.Parse("WooriBank");

        await Assert.ThrowsAsync<ArgumentException>(
            () => launcher.LaunchAsync([serviceId, serviceId], CancellationToken.None));

        Assert.Equal(0, downloader.DownloadCount);
        Assert.Equal(0, processRunner.RunCount);
    }

    [Fact]
    public async Task PreservesCallerCancellationAndRemovesDownloadedArtifact()
    {
        using var cancellation = new CancellationTokenSource();
        var downloader = new FakeBootstrapArtifactDownloader();
        var processRunner = new DelegatingProcessRunner(
            (_, cancellationToken) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<int>(cancellationToken);
            });
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => launcher.LaunchAsync(
                [ServiceId.Parse("WooriBank")],
                cancellation.Token));

        var destinationPath = Assert.IsType<string>(downloader.DestinationPath);
        Assert.False(File.Exists(destinationPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(destinationPath)));
    }

    [Fact]
    public async Task TreatsDownloaderTimeoutAsArtifactRejectionWithoutMaskingCallerCancellation()
    {
        var downloader = new ThrowingBootstrapArtifactDownloader(new TaskCanceledException());
        var processRunner = new CapturingProcessRunner();
        var launcher = CreateLauncher(
            downloader,
            new FakeExecutableSignatureVerifier(ExecutableSignatureVerificationResult.Trusted),
            processRunner);

        await Assert.ThrowsAsync<BootstrapArtifactRejectedException>(
            () => launcher.LaunchAsync([ServiceId.Parse("WooriBank")], CancellationToken.None));

        Assert.Equal(0, processRunner.RunCount);
        var destinationPath = Assert.IsType<string>(downloader.DestinationPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(destinationPath)));
    }

    private static PinnedBootstrapLauncher CreateLauncher(
        IBootstrapArtifactDownloader downloader,
        IExecutableSignatureVerifier signatureVerifier,
        IProcessRunner processRunner) =>
        new(
            downloader,
            signatureVerifier,
            processRunner,
            PrivateTemporaryDirectoryFactory.Instance);

    private static HttpClient CreateHttpClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(new StubHttpMessageHandler(responseFactory))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static HttpResponseMessage CreateContentResponse(
        byte[] body,
        long? declaredContentLength = null,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var content = new StreamContent(new MemoryStream(body, writable: false));
        content.Headers.ContentLength = declaredContentLength ?? body.Length;
        return new HttpResponseMessage(statusCode)
        {
            Content = content,
        };
    }

    private sealed class DelegatingProcessRunner(
        Func<System.Diagnostics.ProcessStartInfo, CancellationToken, Task<int>> runAsync)
        : IProcessRunner
    {
        public Task<int> RunAsync(
            System.Diagnostics.ProcessStartInfo startInfo,
            CancellationToken cancellationToken) => runAsync(startInfo, cancellationToken);
    }

    private sealed class ThrowingBootstrapArtifactDownloader(Exception exception)
        : IBootstrapArtifactDownloader
    {
        public string? DestinationPath { get; private set; }

        public Task<BootstrapArtifactLease> DownloadAsync(
            string destinationPath,
            CancellationToken cancellationToken)
        {
            DestinationPath = destinationPath;
            return Task.FromException<BootstrapArtifactLease>(exception);
        }
    }
}
