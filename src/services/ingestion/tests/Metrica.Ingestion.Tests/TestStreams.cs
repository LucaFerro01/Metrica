using System.Diagnostics;

namespace Metrica.Ingestion.Tests;

/// <summary>
/// Flusso che annulla il token dopo aver letto un certo numero di byte (o alla fine del flusso con soglia
/// <see cref="long.MaxValue"/>), in modo deterministico (nessun timer): il test misura poi quanto tempo il
/// parser impiega a fermarsi.
/// </summary>
internal sealed class CancelAfterBytesStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationTokenSource _cts;
    private readonly long _threshold;
    private long _read;

    public CancelAfterBytesStream(Stream inner, CancellationTokenSource cts, long threshold)
    {
        _inner = inner;
        _cts = cts;
        _threshold = threshold;
    }

    /// <summary>Istante (timestamp di Stopwatch) in cui il token e' stato annullato; 0 se non ancora.</summary>
    public long CanceledAt { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => Track(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Track(_inner.Read(buffer));

    private int Track(int n)
    {
        _read += n;

        // Annulla dopo la soglia di byte o, con soglia massima, alla fine del flusso (lettura che restituisce 0).
        if ((_read >= _threshold || n == 0) && CanceledAt == 0)
        {
            CanceledAt = Stopwatch.GetTimestamp();
            _cts.Cancel();
        }

        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Flusso non scorrevole (come un upload), per verificare che il parser non ne richieda la ricerca.</summary>
internal sealed class ForwardOnlyStream : Stream
{
    private readonly Stream _inner;

    public ForwardOnlyStream(Stream inner) => _inner = inner;

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
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
