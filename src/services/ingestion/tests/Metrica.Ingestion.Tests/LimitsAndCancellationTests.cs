using System.Diagnostics;
using Metrica.Contracts;
using Metrica.Ingestion.Parsing;

namespace Metrica.Ingestion.Tests;

/// <summary>
/// Tetto di punti (REQ-035) e cancellazione (REQ-010) su file grandi generati in memoria.
/// Stessa collezione: i test pesanti non girano in parallelo tra loro (i tempi misurati restano stabili).
/// </summary>
[Collection("Heavy")]
[Trait("Category", "Parsing")]
public class LimitsAndCancellationTests
{
    private static readonly GpxParser Gpx = new();
    private static readonly FitParser Fit = new();
    // Tipicamente 100.000 punti si analizzano in circa 1-2 s (GPX) e 3-4 s (FIT, l'SDK e' il collo di bottiglia): il
    // tetto di 30 s intercetta rallentamenti patologici senza diventare instabile quando la macchina e' carica.
    private static readonly TimeSpan Reasonable = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopWithin = TimeSpan.FromSeconds(1);

    // ---- Tetto di punti

    private static void AssertFull(ParsedActivity a, int count)
    {
        var s = a.Stream;
        Assert.Equal(count, s.TimeOffsetSeconds.Count);
        foreach (var column in new[] { s.Latitude, s.Longitude, s.AltitudeMeters, s.HeartRate, s.PowerWatts, s.Cadence })
        {
            Assert.NotNull(column);
            Assert.Equal(count, column.Count);
        }
    }

    [Fact]
    public void Gpx_Exactly100000Points_AreAccepted_AndParsedInReasonableTime()
    {
        Assert.Equal(100_000, StreamLimits.MaxPoints);
        using var stream = GpxBuilder.Generate(StreamLimits.MaxPoints, allColumns: true);

        var sw = Stopwatch.StartNew();
        var a = Gpx.Parse(stream, CancellationToken.None);
        sw.Stop();

        AssertFull(a, 100_000);
        Assert.Equal(99_999.0, a.ElapsedSeconds);
        Assert.True(sw.Elapsed < Reasonable, $"parsing troppo lento: {sw.Elapsed}");
    }

    [Fact]
    public void Gpx_100001Points_AreRejected()
    {
        using var stream = GpxBuilder.Generate(StreamLimits.MaxPoints + 1, allColumns: true);

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<TooManyPointsException>(() => Gpx.Parse(stream, CancellationToken.None));
        sw.Stop();

        Assert.IsAssignableFrom<ActivityFileException>(ex);
        Assert.True(sw.Elapsed < Reasonable, $"rifiuto troppo lento: {sw.Elapsed}");
    }

    [Fact]
    public void Fit_Exactly100000Records_AreAccepted_AndParsedInReasonableTime()
    {
        var bytes = FitSampleBuilder.Points(StreamLimits.MaxPoints);

        var sw = Stopwatch.StartNew();
        var a = Fit.Parse(new MemoryStream(bytes), CancellationToken.None);
        sw.Stop();

        AssertFull(a, 100_000);
        Assert.Equal(99_999.0, a.ElapsedSeconds);
        Assert.True(sw.Elapsed < Reasonable, $"parsing troppo lento: {sw.Elapsed}");
    }

    [Fact]
    public void Fit_100001Records_AreRejected()
    {
        var bytes = FitSampleBuilder.Points(StreamLimits.MaxPoints + 1);

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<TooManyPointsException>(() => Fit.Parse(new MemoryStream(bytes), CancellationToken.None));
        sw.Stop();

        Assert.IsAssignableFrom<ActivityFileException>(ex);
        Assert.True(sw.Elapsed < Reasonable, $"rifiuto troppo lento: {sw.Elapsed}");
    }

    // ---- Cancellazione

    [Fact]
    public void Gpx_AlreadyCancelledToken_StopsImmediately()
    {
        using var stream = GpxBuilder.Generate(100_000, allColumns: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Gpx.Parse(stream, cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < StopWithin, $"interruzione lenta: {sw.Elapsed}");
        Assert.True(stream.Position < stream.Length / 2, "il parser non doveva leggere il file");
    }

    [Fact]
    public void Gpx_CancellationDuringALargeFile_StopsQuickly()
    {
        using var inner = GpxBuilder.Generate(100_000, allColumns: true);
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterBytesStream(inner, cts, threshold: 2_000_000);

        Assert.ThrowsAny<OperationCanceledException>(() => Gpx.Parse(stream, cts.Token));

        Assert.NotEqual(0, stream.CanceledAt);
        var delay = Stopwatch.GetElapsedTime(stream.CanceledAt);
        Assert.True(delay < StopWithin, $"interruzione lenta: {delay}");
        Assert.True(inner.Position < inner.Length, "il parser doveva fermarsi prima della fine del file");
    }

    [Fact]
    public void Fit_AlreadyCancelledToken_StopsImmediately()
    {
        var bytes = FitSampleBuilder.Points(100_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Fit.Parse(new MemoryStream(bytes), cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < StopWithin, $"interruzione lenta: {sw.Elapsed}");
    }

    [Fact]
    public void Fit_CancellationWhileReadingTheInput_StopsQuickly()
    {
        var bytes = FitSampleBuilder.Points(100_000);
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterBytesStream(new MemoryStream(bytes), cts, threshold: 1_000_000);

        Assert.ThrowsAny<OperationCanceledException>(() => Fit.Parse(stream, cts.Token));

        Assert.NotEqual(0, stream.CanceledAt);
        var delay = Stopwatch.GetElapsedTime(stream.CanceledAt);
        Assert.True(delay < StopWithin, $"interruzione lenta: {delay}");
    }

    [Fact]
    public void Fit_CancellationInTheMiddleOfDecoding_StopsQuickly()
    {
        // Annullamento da un timer mentre il parser decodifica i 100.000 record (la decodifica completa dura
        // alcuni secondi): il parser deve fermarsi molto prima di averla finita.
        var bytes = FitSampleBuilder.Points(100_000);
        using var cts = new CancellationTokenSource();
        var cancelAfter = TimeSpan.FromMilliseconds(200);

        cts.CancelAfter(cancelAfter);
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => Fit.Parse(new MemoryStream(bytes), cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < cancelAfter + TimeSpan.FromSeconds(1.5), $"interruzione lenta: {sw.Elapsed}");
    }

    [Fact]
    public void Fit_CancellationDuringDecoding_StopsQuickly()
    {
        // Il token viene annullato quando il flusso termina: la lettura dei byte e' finita e il parser e' nella
        // fase di decodifica dei 100.000 record, che deve interrompersi al primo messaggio.
        var bytes = FitSampleBuilder.Points(100_000);
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterBytesStream(new MemoryStream(bytes), cts, threshold: long.MaxValue);

        Assert.ThrowsAny<OperationCanceledException>(() => Fit.Parse(stream, cts.Token));

        Assert.NotEqual(0, stream.CanceledAt);
        var delay = Stopwatch.GetElapsedTime(stream.CanceledAt);
        Assert.True(delay < StopWithin, $"interruzione lenta: {delay}");
    }
}
