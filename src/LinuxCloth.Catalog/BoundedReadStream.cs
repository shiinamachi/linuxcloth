namespace LinuxCloth.Catalog;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumBytes;
    private long _bytesRead;

    public BoundedReadStream(Stream inner, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (!inner.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(inner));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        _inner = inner;
        _maximumBytes = maximumBytes;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var bytesRead = _inner.Read(Limit(buffer));
        Record(bytesRead);
        return bytesRead;
    }

    public override int ReadByte()
    {
        var value = _inner.ReadByte();
        if (value >= 0)
        {
            Record(1);
        }

        return value;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await _inner.ReadAsync(Limit(buffer), cancellationToken).ConfigureAwait(false);
        Record(bytesRead);
        return bytesRead;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private Span<byte> Limit(Span<byte> buffer)
    {
        var remainingWithSentinel = Math.Max(0, (_maximumBytes - _bytesRead) + 1);
        return buffer[..(int)Math.Min(buffer.Length, remainingWithSentinel)];
    }

    private Memory<byte> Limit(Memory<byte> buffer)
    {
        var remainingWithSentinel = Math.Max(0, (_maximumBytes - _bytesRead) + 1);
        return buffer[..(int)Math.Min(buffer.Length, remainingWithSentinel)];
    }

    private void Record(int bytesRead)
    {
        _bytesRead += bytesRead;
        if (_bytesRead > _maximumBytes)
        {
            throw new CatalogValidationException(
                $"The document exceeds the {_maximumBytes}-byte limit.");
        }
    }
}
