namespace TriMetric.Ingestion.Parsing;

/// <summary>
/// Sola lettura che controlla il token a ogni lettura: un parser sincrono (XML, FIT) si ferma subito dopo
/// l'annullamento anche mentre salta contenuto che non gli interessa.
/// </summary>
internal sealed class CancellableStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationToken _ct;

    public CancellableStream(Stream inner, CancellationToken ct)
    {
        _inner = inner;
        _ct = ct;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        _ct.ThrowIfCancellationRequested();
        return _inner.Read(buffer);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
