using System.Buffers;

namespace LinuxCloth.Catalog;

public sealed class CatalogSnapshotUpdater
{
    private readonly HttpClient _httpClient;
    private readonly CatalogParser _parser;
    private readonly ICatalogSnapshotStore _store;
    private readonly TimeProvider _timeProvider;

    public CatalogSnapshotUpdater(
        HttpClient httpClient,
        CatalogParser parser,
        ICatalogSnapshotStore store,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(store);

        _httpClient = httpClient;
        _parser = parser;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CatalogSnapshot> UpdateAsync(
        Uri catalogUri,
        string upstreamRepository,
        string upstreamCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogUri);
        if (!catalogUri.IsAbsoluteUri ||
            (catalogUri.Scheme != Uri.UriSchemeHttp && catalogUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "The catalog URI must be an absolute HTTP or HTTPS URI.",
                nameof(catalogUri));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamRepository);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamCommit);

        using var request = new HttpRequestMessage(HttpMethod.Get, catalogUri);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > CatalogParser.MaximumDocumentBytes)
        {
            throw new CatalogValidationException(
                $"The catalog exceeds the {CatalogParser.MaximumDocumentBytes}-byte limit.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalogBytes = await ReadBoundedAsync(responseStream, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = CatalogSnapshot.Create(
            catalogBytes,
            _parser,
            upstreamRepository,
            upstreamCommit,
            _timeProvider.GetUtcNow());
        await _store.PromoteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (destination.Length + bytesRead > CatalogParser.MaximumDocumentBytes)
                {
                    throw new CatalogValidationException(
                        $"The catalog exceeds the {CatalogParser.MaximumDocumentBytes}-byte limit.");
                }

                await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
