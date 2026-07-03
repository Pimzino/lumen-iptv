namespace Lumen.Providers.Xmltv;

/// <summary>
/// Read-only stream that replays a small prefix (consumed while sniffing for gzip magic
/// bytes) before continuing with the inner stream. Forward-only.
/// </summary>
internal sealed class PrefixedStream : Stream
{
    private readonly byte[] _prefix;
    private readonly Stream _inner;
    private int _prefixPosition;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < _prefix.Length)
        {
            var available = Math.Min(_prefix.Length - _prefixPosition, buffer.Length);
            _prefix.AsSpan(_prefixPosition, available).CopyTo(buffer);
            _prefixPosition += available;
            return available;
        }

        return _inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < _prefix.Length)
        {
            return new ValueTask<int>(Read(buffer.Span));
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
