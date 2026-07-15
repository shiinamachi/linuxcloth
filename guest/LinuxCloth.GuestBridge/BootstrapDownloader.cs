using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge;

internal interface IBootstrapArtifactDownloader
{
    Task<BootstrapArtifactLease> DownloadAsync(
        string destinationPath,
        CancellationToken cancellationToken);
}

internal sealed class BootstrapArtifactLease : IDisposable
{
    private FileStream? _readLock;

    public BootstrapArtifactLease(string path, FileStream readLock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _readLock = readLock ?? throw new ArgumentNullException(nameof(readLock));
        if (!_readLock.CanRead || _readLock.CanWrite)
        {
            throw new ArgumentException(
                "The artifact lease requires a read-only file lock.",
                nameof(readLock));
        }
    }

    public string Path { get; }

    public void Dispose()
    {
        _readLock?.Dispose();
        _readLock = null;
    }
}

internal sealed class HttpBootstrapArtifactDownloader : IBootstrapArtifactDownloader
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumRedirects = 2;
    private const string GitHubHost = "github.com";
    private const string GitHubReleasePathPrefix =
        "/yourtablecloth/TableCloth/releases/download/";
    private const string GitHubReleaseAssetsHost = "release-assets.githubusercontent.com";

    private readonly HttpClient _httpClient;
    private readonly Uri _artifactUri;
    private readonly long _expectedSizeBytes;
    private readonly byte[] _expectedSha256;

    public HttpBootstrapArtifactDownloader(HttpClient httpClient)
        : this(
            httpClient,
            new Uri(PinnedSporkRelease.BootstrapUrl, UriKind.Absolute),
            PinnedSporkRelease.BootstrapSizeBytes,
            PinnedSporkRelease.BootstrapSha256)
    {
    }

    internal HttpBootstrapArtifactDownloader(
        HttpClient httpClient,
        Uri artifactUri,
        long expectedSizeBytes,
        string expectedSha256)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _artifactUri = ValidateInitialUri(
            artifactUri ?? throw new ArgumentNullException(nameof(artifactUri)));
        if (expectedSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSizeBytes),
                "The expected artifact size must be positive.");
        }

        _expectedSizeBytes = expectedSizeBytes;
        _expectedSha256 = ParseSha256(expectedSha256);
    }

    public static HttpClient CreateSecureHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 2,
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
            },
            UseCookies = false,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
    }

    public async Task<BootstrapArtifactLease> DownloadAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullDestinationPath = System.IO.Path.GetFullPath(destinationPath);
        FileStream? output = null;
        try
        {
            output = new FileStream(
                fullDestinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            SetPrivateFileMode(fullDestinationPath);

            using var response = await SendWithStrictRedirectsAsync(cancellationToken)
                .ConfigureAwait(false);
            ValidateResponseLength(response.Content.Headers.ContentLength);

            await using var input = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            long totalBytes = 0;

            while (true)
            {
                var bytesRead = await input
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes = checked(totalBytes + bytesRead);
                if (totalBytes > _expectedSizeBytes)
                {
                    throw new BootstrapArtifactRejectedException();
                }

                hash.AppendData(buffer, 0, bytesRead);
                await output
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (totalBytes != _expectedSizeBytes)
            {
                throw new BootstrapArtifactRejectedException();
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            var actualSha256 = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, _expectedSha256))
            {
                throw new BootstrapArtifactRejectedException();
            }

            output.Dispose();
            output = null;
            var readLock = new FileStream(
                fullDestinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            return new BootstrapArtifactLease(fullDestinationPath, readLock);
        }
        catch
        {
            output?.Dispose();
            TryDelete(fullDestinationPath);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendWithStrictRedirectsAsync(
        CancellationToken cancellationToken)
    {
        var requestUri = _artifactUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("linuxcloth-guest-bridge", "1"));
            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                if (response.StatusCode is HttpStatusCode.OK)
                {
                    return response;
                }

                response.Dispose();
                throw new BootstrapArtifactRejectedException();
            }

            using (response)
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    throw new BootstrapArtifactRejectedException();
                }

                var location = response.Headers.Location;
                requestUri = ValidateRedirectUri(
                    location.IsAbsoluteUri ? location : new Uri(requestUri, location));
            }
        }
    }

    private void ValidateResponseLength(long? contentLength)
    {
        if (contentLength is not null && contentLength.Value != _expectedSizeBytes)
        {
            throw new BootstrapArtifactRejectedException();
        }
    }

    private static Uri ValidateInitialUri(Uri uri)
    {
        ValidateCommonUriProperties(uri);
        if (!uri.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(GitHubReleasePathPrefix, StringComparison.Ordinal))
        {
            throw new BootstrapArtifactRejectedException();
        }

        return uri;
    }

    private static Uri ValidateRedirectUri(Uri uri)
    {
        ValidateCommonUriProperties(uri);
        if (!uri.Host.Equals(GitHubReleaseAssetsHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new BootstrapArtifactRejectedException();
        }

        return uri;
    }

    private static void ValidateCommonUriProperties(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new BootstrapArtifactRejectedException();
        }
    }

    private static byte[] ParseSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            var hash = Convert.FromHexString(value);
            return hash.Length == SHA256.HashSizeInBytes
                ? hash
                : throw new ArgumentException("A SHA-256 value must contain 32 bytes.", nameof(value));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("A SHA-256 value must be hexadecimal.", nameof(value), exception);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The enclosing private directory cleanup gets another best-effort attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // The enclosing private directory cleanup gets another best-effort attempt.
        }
    }
}

internal interface IPrivateTemporaryDirectoryFactory
{
    IPrivateTemporaryDirectory Create();
}

internal interface IPrivateTemporaryDirectory : IDisposable
{
    string DirectoryPath { get; }
}

internal sealed class PrivateTemporaryDirectoryFactory : IPrivateTemporaryDirectoryFactory
{
    public static PrivateTemporaryDirectoryFactory Instance { get; } = new();

    private PrivateTemporaryDirectoryFactory()
    {
    }

    public IPrivateTemporaryDirectory Create() => PrivateTemporaryDirectory.Create();
}

internal sealed class PrivateTemporaryDirectory : IPrivateTemporaryDirectory
{
    private bool _disposed;

    private PrivateTemporaryDirectory(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public static PrivateTemporaryDirectory Create()
    {
        var directory = Directory.CreateTempSubdirectory("linuxcloth-guestbridge-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new PrivateTemporaryDirectory(directory.FullName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Cleanup is idempotent.
        }
        catch (IOException)
        {
            // Best-effort cleanup must not replace the process result or cancellation.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup must not replace the process result or cancellation.
        }
    }
}
