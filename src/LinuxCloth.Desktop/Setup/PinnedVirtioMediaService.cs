using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxCloth.Application.ImageBuilding;

namespace LinuxCloth.Desktop.Setup;

public sealed record PinnedVirtioArtifact(
    string Id,
    string Version,
    IReadOnlyList<Uri> Urls,
    long Length,
    string Sha256,
    IReadOnlyList<string> RequiredPaths);

public sealed record VirtioMediaDownloadProgress(
    string Status,
    long BytesReceived,
    long TotalBytes);

public interface IPinnedVirtioMediaService
{
    Task<ImageBuildFileFingerprint?> FindCachedAsync(
        CancellationToken cancellationToken = default);

    Task<ImageBuildFileFingerprint> PrepareAsync(
        IProgress<VirtioMediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class PinnedVirtioManifestSource
{
    public const string ArtifactId = "virtio-win-w11-amd64";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly string _path;

    public PinnedVirtioManifestSource(string? path = null)
    {
        _path = Path.GetFullPath(
            path ?? Path.Combine(AppContext.BaseDirectory, "setup-artifacts", "virtio-win.json"));
    }

    public async Task<PinnedVirtioArtifact> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestDto>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("virtio artifact manifest is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Artifacts is null)
        {
            throw new InvalidDataException("virtio artifact manifest schema is not supported.");
        }

        var artifact = manifest.Artifacts.SingleOrDefault(item =>
            string.Equals(item.Id, ArtifactId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The required virtio artifact is not in the manifest.");
        Validate(artifact);
        return new PinnedVirtioArtifact(
            artifact.Id!,
            artifact.Version!,
            artifact.Urls!.Select(static value => new Uri(value, UriKind.Absolute)).ToArray(),
            artifact.Length,
            artifact.Sha256!.ToLowerInvariant(),
            artifact.RequiredPaths!);
    }

    private static void Validate(ArtifactDto artifact)
    {
        if (!string.Equals(artifact.Id, ArtifactId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(artifact.Version) ||
            artifact.Version.Length > 64 ||
            artifact.Version.Contains("..", StringComparison.Ordinal) ||
            !artifact.Version.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') ||
            artifact.Length is <= 0 or > XorrisoInstallationMediaValidator.MaximumVirtioWinIsoBytes ||
            artifact.Sha256 is not { Length: 64 } ||
            !artifact.Sha256.All(static character => char.IsAsciiHexDigit(character)) ||
            artifact.Urls is not { Count: > 0 } ||
            artifact.RequiredPaths is not { Count: > 0 })
        {
            throw new InvalidDataException("The pinned virtio artifact is invalid.");
        }

        foreach (var value in artifact.Urls)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                !PinnedVirtioMediaService.IsAllowedUri(uri) ||
                uri.AbsolutePath.Contains("/latest-virtio/", StringComparison.Ordinal) ||
                uri.AbsolutePath.Contains("/stable-virtio/", StringComparison.Ordinal) ||
                !uri.AbsolutePath.Contains($"/virtio-win-{artifact.Version}/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The virtio artifact URL is not immutable or allowed.");
            }
        }

        foreach (var requiredPath in artifact.RequiredPaths)
        {
            if (string.IsNullOrWhiteSpace(requiredPath) ||
                requiredPath.Length > 128 ||
                requiredPath.StartsWith('/') ||
                requiredPath.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The virtio artifact has an invalid required path.");
            }
        }
    }

    private sealed class ManifestDto
    {
        public int SchemaVersion { get; init; }

        public List<ArtifactDto>? Artifacts { get; init; }
    }

    private sealed class ArtifactDto
    {
        public string? Id { get; init; }

        public string? Version { get; init; }

        public List<string>? Urls { get; init; }

        public long Length { get; init; }

        public string? Sha256 { get; init; }

        public List<string>? RequiredPaths { get; init; }
    }
}

public sealed class PinnedVirtioMediaService : IPinnedVirtioMediaService, IDisposable
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumRedirects = 3;
    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly PinnedVirtioManifestSource _manifestSource;
    private bool _disposed;

    public PinnedVirtioMediaService(
        string cacheDirectory,
        PinnedVirtioManifestSource? manifestSource = null,
        HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheRoot = Path.GetFullPath(cacheDirectory);
        _manifestSource = manifestSource ?? new PinnedVirtioManifestSource();
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            });
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<ImageBuildFileFingerprint?> FindCachedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var artifact = await _manifestSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        var path = CachePath(artifact);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await VerifyAsync(path, artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            DeleteOwnedFile(path);
            return null;
        }
    }

    public async Task<ImageBuildFileFingerprint> PrepareAsync(
        IProgress<VirtioMediaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var artifact = await _manifestSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        var cached = await FindCachedAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            progress?.Report(new VirtioMediaDownloadProgress("캐시된 장치 드라이버를 사용합니다.", cached.Length, cached.Length));
            return cached;
        }

        var directory = Path.GetDirectoryName(CachePath(artifact))
            ?? throw new InvalidOperationException("virtio cache path has no directory.");
        CreatePrivateDirectory(_cacheRoot);
        CreatePrivateDirectory(Path.Combine(_cacheRoot, "artifacts"));
        CreatePrivateDirectory(Path.Combine(_cacheRoot, "artifacts", artifact.Id));
        CreatePrivateDirectory(directory);

        Exception? lastFailure = null;
        foreach (var uri in artifact.Urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await DownloadAsync(uri, artifact, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or InvalidDataException)
            {
                lastFailure = exception;
            }
        }

        throw new IOException("고정된 Windows 장치 드라이버를 내려받지 못했습니다.", lastFailure);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static bool IsAllowedUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "fedorapeople.org", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private async Task<ImageBuildFileFingerprint> DownloadAsync(
        Uri initialUri,
        PinnedVirtioArtifact artifact,
        IProgress<VirtioMediaDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destination = CachePath(artifact);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".virtio-win-{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await SendAsync(initialUri, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is long declaredLength &&
                declaredLength != artifact.Length)
            {
                throw new InvalidDataException("The virtio artifact length does not match its manifest.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destinationStream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            SetPrivateFileMode(temporary);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long received = 0;
            try
            {
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    received = checked(received + count);
                    if (received > artifact.Length)
                    {
                        throw new InvalidDataException("The virtio artifact exceeded its pinned length.");
                    }

                    hasher.AppendData(buffer, 0, count);
                    await destinationStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(
                        new VirtioMediaDownloadProgress(
                            "Windows 장치 드라이버를 내려받고 있습니다.",
                            received,
                            artifact.Length));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (received != artifact.Length ||
                !string.Equals(
                    Convert.ToHexString(hasher.GetHashAndReset()),
                    artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The virtio artifact did not match its pinned digest and length.");
            }

            destinationStream.Close();
            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                DeleteOwnedFile(temporary);
            }

            var verified = await VerifyAsync(destination, artifact, cancellationToken).ConfigureAwait(false);
            progress?.Report(
                new VirtioMediaDownloadProgress(
                    "Windows 장치 드라이버 준비를 마쳤습니다.",
                    verified.Length,
                    verified.Length));
            return verified;
        }
        finally
        {
            DeleteOwnedFile(temporary);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsAllowedUri(current))
            {
                throw new InvalidDataException("The virtio download target is not allowed.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirect == MaximumRedirects)
            {
                throw new InvalidDataException("The virtio download redirect is invalid.");
            }

            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        throw new InvalidDataException("The virtio download used too many redirects.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private string CachePath(PinnedVirtioArtifact artifact) => Path.Combine(
        _cacheRoot,
        "artifacts",
        artifact.Id,
        artifact.Version,
        "virtio-win.iso");

    private static async Task<ImageBuildFileFingerprint> VerifyAsync(
        string path,
        PinnedVirtioArtifact artifact,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != artifact.Length ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The cached virtio artifact is not a regular file of the pinned length.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(digest, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The cached virtio artifact digest does not match the manifest.");
        }

        file.Refresh();
        return new ImageBuildFileFingerprint(path, digest, file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("A file exists where the virtio artifact cache requires a directory.");
        }

        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The virtio artifact cache refuses symbolic-link directories.");
        }

        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void DeleteOwnedFile(string path)
    {
        try
        {
            if (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
