using System.Diagnostics;
using System.Globalization;
using Dynastream.Fit;
using TriMetric.Contracts;
using TriMetric.Ingestion.Parsing;
using FitDateTime = Dynastream.Fit.DateTime;
using FitSport = Dynastream.Fit.Sport;
using Sport = TriMetric.Contracts.Sport;
using static TriMetric.Ingestion.Tests.GpxBuilder;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// Input ostile o assurdo: annidamento profondo (stack overflow), valori finiti ma enormi, istanti fuori intervallo,
/// troppi giri o sessioni, caso peggiore di dimensione dell'evento.
/// </summary>
[Trait("Category", "Parsing")]
public class HardeningTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly GpxParser Gpx = new();
    private static readonly FitParser Fit = new();

    private static ParsedActivity ParseGpx(string xml) => Gpx.Parse(Bytes(xml), CancellationToken.None);

    private static ParsedActivity ParseFit(byte[] bytes) => Fit.Parse(new MemoryStream(bytes), CancellationToken.None);

    private static string Deep(int levels, string name = "a")
        => string.Concat(Enumerable.Repeat($"<{name}>", levels)) + string.Concat(Enumerable.Repeat($"</{name}>", levels));

    /// <summary>Un'attivita' valida con due punti, in cui <paramref name="firstPointInner"/> e' il contenuto del primo punto.</summary>
    private static string TwoPoints(string firstPointInner, string? time = "2024-05-01T06:00:00Z")
        => Doc(Trk("running", Seg(
            $"<trkpt lat=\"45.0\" lon=\"7.5\">{(time is null ? "" : $"<time>{time}</time>")}{firstPointInner}</trkpt>",
            Pt("45.0001", "7.5", "2024-05-01T06:00:01Z"))));

    // ---- Annidamento profondo (REQ-010: un overflow dello stack ucciderebbe il processo)

    private static string DeepDocument(int which)
    {
        const int n = 100_000;
        const string t = "<time>2024-05-01T06:00:00Z</time>";
        return which switch
        {
            0 => Doc(Trk("running", Seg($"<trkpt lat=\"45\" lon=\"7\">{t}<ele>{Deep(n)}</ele></trkpt>"))),
            1 => Doc(Trk("running", Seg($"<trkpt lat=\"45\" lon=\"7\"><time>{Deep(n)}</time></trkpt>"))),
            2 => Doc($"<trk><type>{Deep(n)}</type>" + Seg(Pt("45", "7", "2024-05-01T06:00:00Z")) + "</trk>"),
            3 => Doc(Trk("running", Seg($"<trkpt lat=\"45\" lon=\"7\">{t}<extensions><gpxtpx:TrackPointExtension><gpxtpx:hr>{Deep(n)}</gpxtpx:hr></gpxtpx:TrackPointExtension></extensions></trkpt>"))),
            4 => Doc(Trk("running", Seg($"<trkpt lat=\"45\" lon=\"7\">{t}<extensions>{Deep(n)}</extensions></trkpt>"))),
            5 => Doc(Trk("running", Seg($"<trkpt lat=\"45\" lon=\"7\">{t}{Deep(n, "unknown")}</trkpt>"))),
            6 => Doc(Deep(n, "wpt") + Trk("running", Seg(Pt("45", "7", "2024-05-01T06:00:00Z")))),
            _ => Doc(Trk("running", Seg(Pt("45", "7", "2024-05-01T06:00:00Z"))) + Deep(n, "trkseg")),
        };
    }

    // 0 ele, 1 time, 2 type, 3 hr, 4 extensions, 5 elemento sconosciuto nel punto, 6 wpt, 7 trkseg fuori da trk
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DeeplyNestedXml_IsRejectedQuicklyWithoutOverflow(int which)
    {
        var xml = DeepDocument(which);

        var sw = Stopwatch.StartNew();
        Assert.Throws<ActivityFileException>(() => ParseGpx(xml));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"rifiuto troppo lento: {sw.Elapsed}");
    }

    [Fact]
    public void NestingDepth_AtTheLimitIsAccepted_BeyondItIsRejected()
    {
        // gpx (profondita' 0) > trk > trkseg > trkpt (3) > extensions (4): k livelli di <a> dentro extensions arrivano
        // a profondita' 4 + k. Il limite e' 32.
        string Nested(int k) => TwoPoints($"<extensions>{Nest(k)}</extensions>");
        static string Nest(int k) => string.Concat(Enumerable.Repeat("<a>", k - 1)) + "<a/>" + string.Concat(Enumerable.Repeat("</a>", k - 1));

        Assert.Equal(2, ParseGpx(Nested(28)).Stream.TimeOffsetSeconds.Count);
        Assert.Throws<ActivityFileException>(() => ParseGpx(Nested(29)));
    }

    // ---- Valori finiti ma enormi, non finiti, fuori scala

    private static void AssertSerializes(ParsedActivity a) => Serialization.ToEnvelopeJson(a); // lancia se i valori non sono finiti

    private static string Ext(string? hr = null, string? cad = null, string? power = null, string? speed = null)
        => "<extensions>"
           + (power is null ? "" : $"<power>{power}</power>")
           + "<gpxtpx:TrackPointExtension>"
           + (hr is null ? "" : $"<gpxtpx:hr>{hr}</gpxtpx:hr>")
           + (cad is null ? "" : $"<gpxtpx:cad>{cad}</gpxtpx:cad>")
           + (speed is null ? "" : $"<gpxtpx:speed>{speed}</gpxtpx:speed>")
           + "</gpxtpx:TrackPointExtension></extensions>";

    [Fact]
    public void HugeAlternatingAltitudeAndHeartRate_DoNotProduceInfinity()
    {
        // Prima: quota alternata 0 / 1e308 e FC 1e308 davano dislivello e FC media infiniti (JSON non serializzabile).
        var points = Enumerable.Range(0, 10).Select(i => Pt(
            (45 + i * 0.0001).ToString("F4", CultureInfo.InvariantCulture), "7.5", T(i), i % 2 == 0 ? "0" : "1e308", Ext(hr: "1e308")));
        var a = ParseGpx(Doc(Trk("running", Seg(points.ToArray()))));

        Assert.True(double.IsFinite(a.ElevationGainMeters ?? 0));
        Assert.Null(a.Summary.AvgHeartRate);
        Assert.Null(a.Summary.MaxHeartRate);
        Assert.Null(a.Stream.HeartRate);
        AssertSerializes(a);
    }

    [Theory]
    [InlineData("1e308")]
    [InlineData("-1e308")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("20000.5")]
    [InlineData("-1000.5")]
    [InlineData("abc")]
    public void ImplausibleAltitude_IsTreatedAsMissing(string ele)
    {
        var a = ParseGpx(TwoPoints($"<ele>{ele}</ele>"));

        Assert.Null(a.Stream.AltitudeMeters);
        Assert.Null(a.ElevationGainMeters);
        AssertSerializes(a);
    }

    [Theory]
    [InlineData("20000", 20000.0)]
    [InlineData("-1000", -1000.0)]
    [InlineData("1.5e2", 150.0)]
    public void PlausibleAltitude_IsKept(string ele, double expected)
    {
        var a = ParseGpx(TwoPoints($"<ele>{ele}</ele>"));

        Assert.Equal(expected, a.Stream.AltitudeMeters![0]);
    }

    [Theory]
    [InlineData("hr", "0")] // FC 0: sentinella dei dispositivi senza lettura, trattata come assente
    [InlineData("hr", "301")]
    [InlineData("hr", "-5")]
    [InlineData("hr", "1e308")]
    [InlineData("hr", "NaN")]
    [InlineData("power", "5001")]
    [InlineData("power", "-1")]
    [InlineData("power", "1e308")]
    [InlineData("cad", "301")]
    [InlineData("cad", "1e308")]
    [InlineData("speed", "100.5")]
    [InlineData("speed", "1e308")]
    [InlineData("speed", "Infinity")]
    public void ImplausibleExtensionValue_IsTreatedAsMissing(string field, string value)
    {
        var inner = field switch
        {
            "hr" => Ext(hr: value),
            "power" => Ext(power: value),
            "cad" => Ext(cad: value),
            _ => Ext(speed: value),
        };

        var a = ParseGpx(TwoPoints(inner));

        Assert.Null(field switch
        {
            "hr" => a.Stream.HeartRate,
            "power" => a.Stream.PowerWatts,
            "cad" => a.Stream.Cadence,
            _ => a.Stream.SpeedMetersPerSecond,
        });
        AssertSerializes(a);
    }

    [Fact]
    public void ValuesAtTheLimits_AreKept()
    {
        var a = ParseGpx(TwoPoints(Ext(hr: "300", cad: "300", power: "5000", speed: "100") + "<ele>0</ele>"));

        Assert.Equal(300.0, a.Stream.HeartRate![0]);
        Assert.Equal(300.0, a.Stream.Cadence![0]);
        Assert.Equal(5000.0, a.Stream.PowerWatts![0]);
        Assert.Equal(100.0, a.Stream.SpeedMetersPerSecond![0]);
        Assert.Equal(0.0, a.Stream.AltitudeMeters![0]);
        AssertSerializes(a);
    }

    [Fact]
    public void HeartRateZero_DoesNotLowerTheAverage()
    {
        var a = ParseGpx(Doc(Trk("running", Seg(
            Pt("45.0", "7.5", T(0), inner: Ext(hr: "0")),
            Pt("45.0001", "7.5", T(1), inner: Ext(hr: "150")),
            Pt("45.0002", "7.5", T(2), inner: Ext(hr: "170"))))));

        Assert.Equal(new double?[] { null, 150, 170 }, a.Stream.HeartRate);
        Assert.Equal(160.0, a.Summary.AvgHeartRate);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    [InlineData("1979-12-31T23:59:59Z")]
    [InlineData("2100-01-01T00:00:01Z")]
    [InlineData("0001-01-01T00:00:00+14:00")]
    public void ImplausibleTimestamps_AreRejected(string time)
    {
        Assert.Throws<ActivityFileException>(() => ParseGpx(TwoPoints("", time)));
    }

    [Theory]
    [InlineData("1980-01-01T00:00:00Z")]
    [InlineData("2099-12-31T23:59:58Z")]
    public void TimestampsInsideTheRange_AreAccepted(string time)
    {
        var a = ParseGpx(TwoPoints("", time));

        Assert.Equal(2, a.Stream.TimeOffsetSeconds.Count);
    }

    [Fact]
    public void Fit_ImplausibleValues_AreTreatedAsMissing()
    {
        var r0 = FitSampleBuilder.RideRecord(0);
        r0.SetPower(60_000); // valido per il tipo FIT, assurdo come potenza
        r0.SetEnhancedAltitude(1_000_000f);
        var r1 = FitSampleBuilder.RideRecord(1);
        r1.SetPower(60_000);
        r1.SetEnhancedAltitude(1_000_000f);

        var a = ParseFit(FitSampleBuilder.Build([FitSampleBuilder.FileId(), r0, r1]));

        Assert.Null(a.Stream.PowerWatts);
        Assert.Null(a.Summary.AvgPowerWatts);
        Assert.Null(a.Stream.AltitudeMeters);
        Assert.Null(a.ElevationGainMeters);
        AssertSerializes(a);
    }

    [Fact]
    public void Fit_ImplausibleSessionSummary_FallsBackToTheRecords()
    {
        var bytes = FitSampleBuilder.Build(
        [
            FitSampleBuilder.FileId(),
            FitSampleBuilder.RideRecord(0),
            FitSampleBuilder.RideRecord(1),
            FitSampleBuilder.Session(FitSport.Cycling, FitSampleBuilder.Start, 1, 1, 8, avgPower: 60_000),
        ]);

        var a = ParseFit(bytes);

        Assert.Equal(100.5, a.Summary.AvgHeartRate); // dai record: 100 e 101
        Assert.Equal(101.0, a.Summary.MaxHeartRate);
        Assert.Equal(151.0, a.Summary.AvgPowerWatts); // 150 e 152
        Assert.Equal(90.0, a.Summary.AvgCadence);
        AssertSerializes(a);
    }

    [Fact]
    public void Fit_TimestampBeyond2100_IsRejected()
    {
        var r = FitSampleBuilder.RideRecord(0);
        r.SetTimestamp(new FitDateTime(uint.MaxValue - 10)); // anno 2125

        Assert.Throws<ActivityFileException>(() => ParseFit(FitSampleBuilder.Build([FitSampleBuilder.FileId(), r])));
    }

    // ---- Tetti su giri, segmenti e sessioni

    private static string SegmentsDoc(int count)
        => Doc(Trk("running", Enumerable.Range(0, count)
            .Select(i => Seg(Pt("45.0", "7.5", T(i))))
            .ToArray()));

    [Fact]
    public void Gpx_UpTo1000Segments_AreAccepted_MoreAreRejected()
    {
        Assert.Equal(1000, ParseGpx(SegmentsDoc(1000)).Laps.Count);
        Assert.Throws<ActivityFileException>(() => ParseGpx(SegmentsDoc(1001)));
    }

    [Fact]
    public void Fit_UpTo1000Laps_AreAccepted_MoreAreRejected()
    {
        Assert.Equal(1000, ParseFit(FitFile(laps: 1000, sessions: 1)).Laps.Count);
        Assert.Throws<ActivityFileException>(() => ParseFit(FitFile(laps: 1001, sessions: 1)));
    }

    [Fact]
    public void Fit_UpTo100Sessions_AreAccepted_MoreAreRejected()
    {
        var a = ParseFit(FitFile(laps: 1, sessions: 100));

        Assert.Equal(Sport.Other, a.Sport);
        Assert.Equal(100 * 60.0, a.ElapsedSeconds);
        Assert.Throws<ActivityFileException>(() => ParseFit(FitFile(laps: 1, sessions: 101)));
    }

    private static byte[] FitFile(int laps, int sessions)
    {
        var msgs = new List<Mesg> { FitSampleBuilder.FileId(), FitSampleBuilder.RideRecord(0), FitSampleBuilder.RideRecord(1) };
        for (var i = 0; i < laps; i++)
        {
            msgs.Add(FitSampleBuilder.Lap(FitSampleBuilder.Start.AddSeconds(i), 1, 1));
        }

        for (var i = 0; i < sessions; i++)
        {
            msgs.Add(FitSampleBuilder.Session(FitSport.Cycling, FitSampleBuilder.Start.AddSeconds(i), 60, 60, 10));
        }

        return FitSampleBuilder.Build(msgs);
    }

    // ---- Dimensione dell'evento nel caso peggiore (DEC-012: sotto gli 8 MB del limite NATS)

    [Fact]
    public void WorstCaseEvent_100000PointsAllColumnsAnd1000Laps_StaysUnder8MiB()
    {
        using var stream = GenerateWorstCase();

        var a = Gpx.Parse(stream, CancellationToken.None);

        Assert.Equal(100_000, a.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(1000, a.Laps.Count);
        Assert.NotNull(a.Stream.Latitude);
        Assert.NotNull(a.Stream.Longitude);
        Assert.NotNull(a.Stream.AltitudeMeters);
        Assert.NotNull(a.Stream.HeartRate);
        Assert.NotNull(a.Stream.PowerWatts);
        Assert.NotNull(a.Stream.Cadence);
        Assert.NotNull(a.Stream.SpeedMetersPerSecond);
        var json = Serialization.ToEnvelopeJson(a);
        output.WriteLine($"Evento nel caso peggiore: {json.Length} byte (limite 8.388.608)");
        Assert.True(json.Length < 8 * 1024 * 1024, $"evento di {json.Length} byte");
    }

    /// <summary>
    /// 100.000 punti in 1000 segmenti da 100, con il massimo di cifre in ogni colonna: coordinate negative a 7
    /// decimali, quota negativa, FC e cadenza a 3 cifre, potenza a 4, velocita' 99,xxx, offset di tempo a 5 cifre e 3 decimali.
    /// </summary>
    private static MemoryStream GenerateWorstCase()
    {
        var ms = new MemoryStream();
        using (var w = new StreamWriter(ms, new System.Text.UTF8Encoding(false), 64 * 1024, leaveOpen: true))
        {
            w.Write($"<?xml version=\"1.0\"?><gpx version=\"1.1\" creator=\"t\" xmlns=\"{Ns11}\" xmlns:gpxtpx=\"{TpxNs}\"><trk><type>running</type>");
            var start = new System.DateTime(2024, 5, 1, 6, 0, 0, System.DateTimeKind.Utc);
            for (var s = 0; s < 1000; s++)
            {
                w.Write("<trkseg>");
                for (var j = 0; j < 100; j++)
                {
                    var i = s * 100 + j;
                    var lat = -(80 + (i % 9) + 0.9999999 - (j * 1e-7));
                    var lon = -(170 + (i % 9) + 0.9999999 - (j * 1e-7));
                    var time = start.AddMilliseconds(i * 999L).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    w.Write($"<trkpt lat=\"{lat.ToString("F7", CultureInfo.InvariantCulture)}\" lon=\"{lon.ToString("F7", CultureInfo.InvariantCulture)}\">");
                    w.Write($"<ele>{(-(900 + (j % 10) * 0.1 + 0.05)).ToString("F2", CultureInfo.InvariantCulture)}</ele><time>{time}</time>");
                    w.Write($"<extensions><power>{4990 + (j % 10)}</power><gpxtpx:TrackPointExtension><gpxtpx:hr>{290 + (j % 10)}</gpxtpx:hr><gpxtpx:cad>{290 + (j % 10)}</gpxtpx:cad><gpxtpx:speed>{(99 + (j % 10) * 0.0999).ToString("F4", CultureInfo.InvariantCulture)}</gpxtpx:speed></gpxtpx:TrackPointExtension></extensions></trkpt>");
                }

                w.Write("</trkseg>");
            }

            w.Write("</trk></gpx>");
        }

        ms.Position = 0;
        return ms;
    }

    // ---- Limite di memoria del FIT

    [Fact]
    public void Fit_InputLargerThan32MiB_IsRejectedWithoutReadingMore()
    {
        // Flusso infinito di zeri: il parser deve fermarsi al tetto di 32 MiB, non leggere tutto.
        var stream = new EndlessZerosStream();

        Assert.Throws<ActivityFileException>(() => Fit.Parse(stream, CancellationToken.None));

        Assert.InRange(stream.BytesRead, 32L * 1024 * 1024, 32L * 1024 * 1024 + 100_000);
    }

    private sealed class EndlessZerosStream : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            BytesRead += count;
            return count;
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

    // ---- Soglia del tempo in movimento (0,5 m/s)

    private static ParsedActivity OneIntervalAtSpeed(double metersPerSecond)
    {
        // Due punti a 1000 s di distanza sullo stesso meridiano, a distanza = velocita' * 1000 m.
        var degrees = metersPerSecond * 1000 / (6_371_008.8 * Math.PI / 180);
        var lat = (45 + degrees).ToString("F10", CultureInfo.InvariantCulture);
        return ParseGpx(Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0)), Pt(lat, "7.5", T(1000))))));
    }

    [Fact]
    public void MovingThreshold_IsHalfAMeterPerSecond()
    {
        Assert.Equal(0.0, OneIntervalAtSpeed(0.499).MovingSeconds);
        Assert.Equal(1000.0, OneIntervalAtSpeed(0.501).MovingSeconds);
    }
}
