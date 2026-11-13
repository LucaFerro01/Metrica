using TriMetric.Contracts;
using TriMetric.Ingestion.Parsing;
using static TriMetric.Ingestion.Tests.GpxBuilder;

namespace TriMetric.Ingestion.Tests;

[Trait("Category", "Parsing")]
public class GpxParserTests
{
    /// <summary>Metri per grado di meridiano con il raggio medio 6.371.008,8 m: R * pi / 180.</summary>
    private const double MetersPerDegree = 6_371_008.8 * Math.PI / 180;

    private static readonly GpxParser Parser = new();

    private static ParsedActivity Parse(string xml) => Parser.Parse(Bytes(xml), CancellationToken.None);

    private static ParsedActivity ParseSample(string name)
    {
        using var stream = SampleFiles.Open(name);
        return Parser.Parse(stream, CancellationToken.None);
    }

    [Fact]
    public void RunSample_HasTheValuesVerifiedByHand()
    {
        // run.gpx: 61 punti a 1 Hz da 2024-05-01T06:00:00Z, <type>running</type>, un solo segmento, longitudine costante.
        // Latitudine: sale di 0,00003 gradi per secondo fino a i = 30 (45,0009), resta ferma da i = 30 a i = 40
        // (10 intervalli fermi), poi riparte: 50 intervalli in movimento = 0,0015 gradi.
        var a = ParseSample("run.gpx");

        Assert.Equal(Sport.Run, a.Sport);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero), a.StartTime);
        Assert.Equal(TimeSpan.Zero, a.StartTime.Offset);
        Assert.Equal(60.0, a.ElapsedSeconds);
        Assert.Equal(50.0, a.MovingSeconds); // 60 s meno i 10 intervalli fermi (velocita' 0 < 0,5 m/s)
        Assert.Equal(0.0015 * MetersPerDegree, a.DistanceMeters, 0.001); // circa 166,792 m
        Assert.Equal(10.0, a.ElevationGainMeters); // 100 -> 110 in salita (+10), poi 110 -> 100 in discesa (non conta)
        Assert.Equal(150.0, a.Summary.AvgHeartRate); // FC = 120 + i per i = 0..60: media 150
        Assert.Equal(180.0, a.Summary.MaxHeartRate);
        Assert.Equal(85.0, a.Summary.AvgCadence); // 30 punti a 80, uno a 85, 30 a 90
        Assert.Null(a.Summary.AvgPowerWatts);
        Assert.Empty(a.Laps); // un solo segmento: nessun giro
    }

    [Fact]
    public void RunSample_StreamColumns()
    {
        var s = ParseSample("run.gpx").Stream;

        Assert.Equal(61, s.TimeOffsetSeconds.Count);
        Assert.Equal(0.0, s.TimeOffsetSeconds[0]);
        Assert.Equal(60.0, s.TimeOffsetSeconds[60]);
        Assert.NotNull(s.Latitude);
        Assert.NotNull(s.Longitude);
        Assert.NotNull(s.AltitudeMeters);
        Assert.NotNull(s.HeartRate);
        Assert.NotNull(s.Cadence);
        Assert.Null(s.PowerWatts);
        Assert.Null(s.SpeedMetersPerSecond);
        Assert.Equal(45.0, s.Latitude![0]);
        Assert.Equal(45.0009, s.Latitude[30]);
        Assert.Equal(45.0009, s.Latitude[40]);
        Assert.Equal(45.0015, s.Latitude[60]);
        Assert.Equal(7.5, s.Longitude![17]);
        Assert.Equal(110.0, s.AltitudeMeters![25]);
        Assert.Equal(120.0, s.HeartRate![0]);
        Assert.Equal(180.0, s.HeartRate[60]);
        Assert.Equal(85.0, s.Cadence![30]);
    }

    [Fact]
    public void NoElevationSample_HasNullAltitudeAndNullGain()
    {
        // gpx-no-elevation.gpx: 11 punti ogni 10 s, <type>cycling</type>, latitudine +0,0001 gradi a punto
        // (circa 11,12 m ogni 10 s = 1,11 m/s, sopra la soglia di 0,5 m/s): durata 100 s, tutta in movimento.
        var a = ParseSample("gpx-no-elevation.gpx");

        Assert.Equal(Sport.Ride, a.Sport);
        Assert.Equal(100.0, a.ElapsedSeconds);
        Assert.Equal(100.0, a.MovingSeconds);
        Assert.Equal(0.001 * MetersPerDegree, a.DistanceMeters, 0.001); // circa 111,195 m
        Assert.Null(a.ElevationGainMeters);
        Assert.Null(a.Stream.AltitudeMeters);
        Assert.NotNull(a.Stream.Latitude);
        Assert.Null(a.Stream.HeartRate);
        Assert.Null(a.Summary.AvgHeartRate);
        Assert.Null(a.Summary.MaxHeartRate);
        Assert.Null(a.Summary.AvgCadence);
        Assert.Null(a.Summary.AvgPowerWatts);
    }

    [Fact]
    public void PointWithoutTime_IsRejected()
    {
        using var stream = SampleFiles.Open("gpx-no-time.gpx");
        var ex = Assert.Throws<ActivityFileException>(() => Parser.Parse(stream, CancellationToken.None));
        Assert.IsNotType<TooManyPointsException>(ex);
    }

    [Fact]
    public void MultipleTracksAndSegments_AreMergedInTimeOrder()
    {
        // Tre segmenti in due tracce, scritti fuori ordine. Punti in ordine di tempo: t = 0, 1 | 10, 11 | 20, 21 con
        // latitudine 45 + 0,0001 k (k = 0..5): il percorso ordinato e' un unico tratto di meridiano.
        double Lat(int k) => 45 + 0.0001 * k;
        string P(int k, double t) => Pt(Lat(k).ToString("F4", System.Globalization.CultureInfo.InvariantCulture), "7.5", T(t));
        var xml = Doc(
            Trk("running", Seg(P(2, 10), P(3, 11))) +
            Trk(null, Seg(P(0, 0), P(1, 1)), Seg(P(4, 20), P(5, 21))));

        var a = Parse(xml);

        Assert.Equal(new double[] { 0, 1, 10, 11, 20, 21 }, a.Stream.TimeOffsetSeconds);
        Assert.Equal(new double?[] { 45.0, 45.0001, 45.0002, 45.0003, 45.0004, 45.0005 }, a.Stream.Latitude);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero), a.StartTime);
        Assert.Equal(21.0, a.ElapsedSeconds);
        Assert.Equal(Sport.Run, a.Sport); // il tipo e' quello della prima traccia che lo dichiara
        Assert.Equal(0.0005 * MetersPerDegree, a.DistanceMeters, 0.001);

        // Regola dei giri: un giro per segmento (non vuoto) quando i segmenti sono piu' di uno, in ordine di inizio.
        Assert.Equal(3, a.Laps.Count);
        Assert.Equal(
            new[] { 0.0, 10.0, 20.0 },
            a.Laps.Select(l => (l.StartTime - a.StartTime).TotalSeconds).ToArray());
        Assert.All(a.Laps, l => Assert.Equal(1.0, l.ElapsedSeconds));
        Assert.All(a.Laps, l => Assert.Equal(0.0001 * MetersPerDegree, l.DistanceMeters, 0.001));
    }

    [Fact]
    public void UnorderedPoints_AreSortedByTime()
    {
        string P(double t, string lat) => Pt(lat, "7.5", T(t));
        var xml = Doc(Trk("run", Seg(P(3, "45.0003"), P(1, "45.0001"), P(2, "45.0002"), P(0, "45.0000"))));

        var a = Parse(xml);

        Assert.Equal(new double[] { 0, 1, 2, 3 }, a.Stream.TimeOffsetSeconds);
        Assert.Equal(new double?[] { 45.0, 45.0001, 45.0002, 45.0003 }, a.Stream.Latitude);
        Assert.Equal(3.0, a.ElapsedSeconds);
        Assert.Equal(0.0003 * MetersPerDegree, a.DistanceMeters, 0.001);
        Assert.Empty(a.Laps);
    }

    [Fact]
    public void GarminExtensions_ReadHeartRateAndCadence()
    {
        var xml = Doc(Trk("running", Seg(
            Pt("45.0", "7.5", T(0), inner: Tpx("140", "80")),
            Pt("45.0001", "7.5", T(1), inner: Tpx("150", null)),
            Pt("45.0002", "7.5", T(2), inner: Tpx(null, "90")))));

        var a = Parse(xml);

        Assert.Equal(new double?[] { 140, 150, null }, a.Stream.HeartRate);
        Assert.Equal(new double?[] { 80, null, 90 }, a.Stream.Cadence);
        Assert.Equal(145.0, a.Summary.AvgHeartRate);
        Assert.Equal(150.0, a.Summary.MaxHeartRate);
        Assert.Equal(85.0, a.Summary.AvgCadence);
    }

    [Fact]
    public void PowerAndSpeedExtensions_AreReadAndUnknownOnesIgnored()
    {
        var xml = Doc(Trk("cycling", Seg(
            Pt("45.0", "7.5", T(0), inner: "<extensions><power>200</power><gpxtpx:TrackPointExtension><gpxtpx:speed>5.5</gpxtpx:speed></gpxtpx:TrackPointExtension><unknown:foo xmlns:unknown=\"urn:x\"><deep>1</deep></unknown:foo></extensions>"),
            Pt("45.0001", "7.5", T(1), inner: "<extensions><power>300</power><gpxtpx:TrackPointExtension><gpxtpx:speed>6.5</gpxtpx:speed></gpxtpx:TrackPointExtension></extensions>"))));

        var a = Parse(xml);

        Assert.Equal(new double?[] { 200, 300 }, a.Stream.PowerWatts);
        Assert.Equal(new double?[] { 5.5, 6.5 }, a.Stream.SpeedMetersPerSecond);
        Assert.Equal(250.0, a.Summary.AvgPowerWatts);
        Assert.Null(a.Stream.HeartRate);
    }

    [Fact]
    public void Gpx10_NamespaceAndSpeedElement_AreSupported()
    {
        // In GPX 1.0 speed e' un elemento figlio diretto di trkpt.
        var xml = Doc(
            Trk("running", Seg(
                Pt("45.0", "7.5", T(0), "100", "<speed>2.5</speed>"),
                Pt("45.0001", "7.5", T(10), "101", "<speed>3.5</speed>"))),
            ns: Ns10);

        var a = Parse(xml);

        Assert.Equal(Sport.Run, a.Sport);
        Assert.Equal(2, a.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(new double?[] { 2.5, 3.5 }, a.Stream.SpeedMetersPerSecond);
        Assert.Equal(1.0, a.ElevationGainMeters);
    }

    [Fact]
    public void Gpx11_Namespace_IsSupported()
    {
        var a = Parse(Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0)), Pt("45.0001", "7.5", T(1)))), ns: Ns11));
        Assert.Equal(2, a.Stream.TimeOffsetSeconds.Count);
    }

    [Fact]
    public void DocumentWithoutNamespace_IsAccepted_ForeignNamespaceIsNot()
    {
        var points = Trk("running", Seg(Pt("45.0", "7.5", T(0)), Pt("45.0001", "7.5", T(1))));
        var noNs = $"<gpx version=\"1.1\">{points}</gpx>";
        Assert.Equal(2, Parse(noNs).Stream.TimeOffsetSeconds.Count);

        Assert.Throws<ActivityFileException>(() => Parse(Doc(points, ns: "urn:not-gpx")));
        Assert.Throws<ActivityFileException>(() => Parse($"<kml>{points}</kml>"));
    }

    [Theory]
    [InlineData("running", Sport.Run)]
    [InlineData("run", Sport.Run)]
    [InlineData("Running", Sport.Run)]
    [InlineData("cycling", Sport.Ride)]
    [InlineData("biking", Sport.Ride)]
    [InlineData("ride", Sport.Ride)]
    [InlineData("swimming", Sport.Swim)]
    [InlineData("swim", Sport.Swim)]
    [InlineData("hiking", Sport.Other)]
    [InlineData("", Sport.Other)]
    [InlineData(null, Sport.Other)]
    public void SportIsMappedFromTheTrackType(string? type, Sport expected)
    {
        var a = Parse(Doc(Trk(type, Seg(Pt("45.0", "7.5", T(0)), Pt("45.0001", "7.5", T(1))))));
        Assert.Equal(expected, a.Sport);
    }

    [Fact]
    public void SinglePoint_IsAccepted_WithZeroDurationAndDistance()
    {
        var a = Parse(Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0))))));

        Assert.Single(a.Stream.TimeOffsetSeconds);
        Assert.Equal(0.0, a.ElapsedSeconds);
        Assert.Equal(0.0, a.MovingSeconds);
        Assert.Equal(0.0, a.DistanceMeters);
    }

    [Fact]
    public void RepeatedTimestamps_AreKeptWithoutDividingByZero()
    {
        var a = Parse(Doc(Trk("running", Seg(
            Pt("45.0", "7.5", T(0)),
            Pt("45.0", "7.5", T(0)),
            Pt("45.0001", "7.5", T(1))))));

        Assert.Equal(new double[] { 0, 0, 1 }, a.Stream.TimeOffsetSeconds);
        Assert.True(double.IsFinite(a.MovingSeconds));
        Assert.Equal(1.0, a.MovingSeconds);
    }

    [Theory]
    [InlineData("<gpx")]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg><trkpt lat=\"1\" lon=\"2\"><time>2024-05-01T06:00:00Z</time></trkpt>")] // troncato
    [InlineData("<gpx xmlns=\"http://www.topografix.com/GPX/1/1\"><trk></gpx>")] // tag non chiuso
    public void MalformedXml_IsRejected(string xml)
    {
        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Fact]
    public void BinaryGarbage_IsRejected()
    {
        var bytes = new byte[2048];
        new Random(1).NextBytes(bytes);

        Assert.Throws<ActivityFileException>(() => Parser.Parse(new MemoryStream(bytes), CancellationToken.None));
    }

    [Theory]
    [InlineData("91", "7.5")]
    [InlineData("-90.0001", "7.5")]
    [InlineData("45", "180.5")]
    [InlineData("45", "-181")]
    [InlineData("abc", "7.5")]
    [InlineData("45", "")]
    [InlineData("NaN", "7.5")]
    [InlineData("45", "Infinity")]
    public void InvalidCoordinates_AreRejected(string lat, string lon)
    {
        var xml = Doc(Trk("running", Seg(Pt(lat, lon, T(0)), Pt("45.0", "7.5", T(1)))));
        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Fact]
    public void MissingCoordinateAttributes_AreRejected()
    {
        var xml = Doc("<trk><trkseg><trkpt lat=\"45.0\"><time>2024-05-01T06:00:00Z</time></trkpt></trkseg></trk>");
        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("")]
    public void InvalidTime_IsRejected(string time)
    {
        var xml = Doc(Trk("running", Seg(Pt("45.0", "7.5", time))));
        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Theory]
    [InlineData("<trk><trkseg></trkseg></trk>")]
    [InlineData("<trk></trk>")]
    [InlineData("<wpt lat=\"1\" lon=\"2\"/>")]
    [InlineData("")]
    public void FileWithoutTrackPoints_IsRejected(string body)
    {
        Assert.Throws<ActivityFileException>(() => Parse(Doc(body)));
    }

    [Fact]
    public void WaypointsAndRoutes_AreIgnored()
    {
        var xml = Doc(
            "<wpt lat=\"10\" lon=\"10\"><time>2020-01-01T00:00:00Z</time></wpt>" +
            "<rte><rtept lat=\"11\" lon=\"11\"/></rte>" +
            Trk("running", Seg(Pt("45.0", "7.5", T(0)), Pt("45.0001", "7.5", T(1)))));

        var a = Parse(xml);

        Assert.Equal(2, a.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero), a.StartTime);
    }

    [Fact]
    public void TimesWithOffsets_AreConvertedToUtc()
    {
        var xml = Doc(Trk("running", Seg(
            Pt("45.0", "7.5", "2024-05-01T08:00:00+02:00"),
            Pt("45.0001", "7.5", "2024-05-01T06:00:10Z"))));

        var a = Parse(xml);

        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero), a.StartTime);
        Assert.Equal(TimeSpan.Zero, a.StartTime.Offset);
        Assert.Equal(10.0, a.ElapsedSeconds);
    }

    [Fact]
    public void NonSeekableStream_IsSupported()
    {
        var xml = Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0)), Pt("45.0001", "7.5", T(1)))));

        var a = Parser.Parse(new ForwardOnlyStream(Bytes(xml)), CancellationToken.None);

        Assert.Equal(2, a.Stream.TimeOffsetSeconds.Count);
    }

    // ---- XML non attendibile (DTD, entita', XXE): qualunque DOCTYPE e' rifiutato, senza espansione ne' accessi esterni.

    [Fact]
    public void BillionLaughs_IsRejectedQuicklyWithoutExpansion()
    {
        const string prolog =
            "<!DOCTYPE gpx [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">" +
            "<!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\"><!ENTITY d \"&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;\">" +
            "<!ENTITY e \"&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;\"><!ENTITY f \"&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;\">" +
            "<!ENTITY g \"&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;\"><!ENTITY h \"&g;&g;&g;&g;&g;&g;&g;&g;&g;&g;\">]>";
        var xml = Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0)))).Replace("<type>running</type>", "<type>&h;</type>"), prolog: prolog);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Throws<ActivityFileException>(() => Parse(xml));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"rifiuto troppo lento: {sw.Elapsed}");
    }

    [Fact]
    public void ExternalEntity_Xxe_IsRejectedAndNeverRead()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"trimetric-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "SEGRETO-XXE");
        try
        {
            var uri = new Uri(secret).AbsoluteUri;
            var prolog = $"<!DOCTYPE gpx [<!ENTITY xxe SYSTEM \"{uri}\">]>";
            var xml = Doc(Trk("running", Seg(Pt("45.0", "7.5", T(0)))).Replace("<type>running</type>", "<type>&xxe;</type>"), prolog: prolog);

            var ex = Assert.Throws<ActivityFileException>(() => Parse(xml));

            Assert.DoesNotContain("SEGRETO-XXE", ex.ToString());
            Assert.DoesNotContain(secret, ex.ToString());
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void ExternalDtd_IsRejected()
    {
        var xml = Doc(
            Trk("running", Seg(Pt("45.0", "7.5", T(0)))),
            prolog: "<!DOCTYPE gpx SYSTEM \"http://127.0.0.1:1/evil.dtd\">");

        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Fact]
    public void AnyDtd_EvenWithAHarmlessInternalEntity_IsRejected()
    {
        // Politica: nessun DOCTYPE. Un'entita' interna innocua non deve passare (un GPX reale non ne ha).
        var xml = Doc(
            Trk("running", Seg(Pt("45.0", "7.5", T(0)))).Replace("<type>running</type>", "<type>&n;</type>"),
            prolog: "<!DOCTYPE gpx [<!ENTITY n \"running\">]>");

        Assert.Throws<ActivityFileException>(() => Parse(xml));
    }

    [Fact]
    public void ExceptionMessages_DoNotLeakFileContent()
    {
        var ex = Assert.Throws<ActivityFileException>(
            () => Parse(Doc(Trk("running", Seg(Pt("45.123456", "7.654321", "SEGRETO-ORARIO"))))));

        Assert.DoesNotContain("SEGRETO-ORARIO", ex.ToString());
        Assert.DoesNotContain("45.123456", ex.ToString());
        Assert.Null(ex.InnerException);
    }
}
