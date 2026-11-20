using Dynastream.Fit;
using Metrica.Contracts;
using Metrica.Ingestion.Parsing;
using FitDateTime = Dynastream.Fit.DateTime;
using Sport = Metrica.Contracts.Sport;
using FitSport = Dynastream.Fit.Sport;

namespace Metrica.Ingestion.Tests;

[Trait("Category", "Parsing")]
public class FitParserTests
{
    /// <summary>Metri per grado di meridiano con il raggio medio 6.371.008,8 m: R * pi / 180.</summary>
    private const double MetersPerDegree = 6_371_008.8 * Math.PI / 180;

    /// <summary>Gradi per semicerchio: 180 / 2^31.</summary>
    private const double DegreesPerSemicircle = 180.0 / 2_147_483_648.0;

    private static readonly DateTimeOffset Start = new(2024, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly FitParser Parser = new();

    private static ParsedActivity Parse(byte[] bytes) => Parser.Parse(new MemoryStream(bytes), CancellationToken.None);

    [Fact]
    public void Ride_SessionValuesAreUsed()
    {
        // Sessione scritta dal generatore: Cycling, 60 s trascorsi e in movimento, 510 m (60 s * 8,5 m/s),
        // salita 12 m, FC media 130 / massima 160, potenza media 210, cadenza media 90.
        var a = Parse(FitSampleBuilder.Ride());

        Assert.Equal(Sport.Ride, a.Sport);
        Assert.Equal(Start, a.StartTime);
        Assert.Equal(TimeSpan.Zero, a.StartTime.Offset);
        Assert.Equal(60.0, a.ElapsedSeconds);
        Assert.Equal(60.0, a.MovingSeconds);
        Assert.Equal(510.0, a.DistanceMeters);
        Assert.Equal(12.0, a.ElevationGainMeters);
        Assert.Equal(130.0, a.Summary.AvgHeartRate);
        Assert.Equal(160.0, a.Summary.MaxHeartRate);
        Assert.Equal(210.0, a.Summary.AvgPowerWatts);
        Assert.Equal(90.0, a.Summary.AvgCadence);
    }

    [Fact]
    public void Ride_LapsComeFromLapMessages()
    {
        var a = Parse(FitSampleBuilder.Ride());

        Assert.Equal(2, a.Laps.Count);
        Assert.Equal(Start, a.Laps[0].StartTime);
        Assert.Equal(30.0, a.Laps[0].ElapsedSeconds);
        Assert.Equal(255.0, a.Laps[0].DistanceMeters);
        Assert.Equal(Start.AddSeconds(30), a.Laps[1].StartTime);
        Assert.Equal(30.0, a.Laps[1].ElapsedSeconds);
        Assert.Equal(255.0, a.Laps[1].DistanceMeters);
    }

    [Fact]
    public void Ride_StreamColumns()
    {
        var s = Parse(FitSampleBuilder.Ride()).Stream;

        Assert.Equal(61, s.TimeOffsetSeconds.Count);
        Assert.Equal(0.0, s.TimeOffsetSeconds[0]);
        Assert.Equal(60.0, s.TimeOffsetSeconds[60]);
        Assert.Equal(100.0, s.HeartRate![0]);
        Assert.Equal(160.0, s.HeartRate[60]);
        Assert.Equal(150.0, s.PowerWatts![0]);
        Assert.Equal(270.0, s.PowerWatts[60]);
        Assert.Equal(90.0, s.Cadence![5]);
        Assert.Equal(8.5, s.SpeedMetersPerSecond![5]);
        Assert.Equal(100.0, s.AltitudeMeters![0]);
        Assert.Equal(112.0, s.AltitudeMeters[60]);
    }

    [Fact]
    public void Semicircles_AreConvertedToDegrees()
    {
        var s = Parse(FitSampleBuilder.Ride()).Stream;

        // 2^29 semicerchi = 45 gradi esatti; 2^28 = 22,5 gradi esatti.
        Assert.Equal(45.0, s.Latitude![0]);
        Assert.Equal(22.5, s.Longitude![0]);
        // Record 60: 2^29 + 60 * 912 semicerchi, arrotondato a 7 decimali.
        var expected = Math.Round(45.0 + 54_720 * DegreesPerSemicircle, 7);
        Assert.Equal(expected, s.Latitude[60]);
        Assert.Equal(45.0045866, s.Latitude[60]);
    }

    [Fact]
    public void SampleFileInRepository_MatchesTheDeterministicBuilder()
    {
        // Samples/ride.fit e' l'output di FitSampleBuilder.Ride(): se il generatore cambia, il file va rigenerato.
        var onDisk = System.IO.File.ReadAllBytes(SampleFiles.PathOf("ride.fit"));

        Assert.Equal(FitSampleBuilder.Ride(), onDisk);

        using var stream = SampleFiles.Open("ride.fit");
        var a = Parser.Parse(stream, CancellationToken.None);
        Assert.Equal(Sport.Ride, a.Sport);
        Assert.Equal(130.0, a.Summary.AvgHeartRate);
        Assert.Equal(210.0, a.Summary.AvgPowerWatts);
    }

    [Fact]
    public void Ride_EnhancedFields_GiveTheSameValues()
    {
        var plain = Parse(FitSampleBuilder.Ride());
        var enhanced = Parse(FitSampleBuilder.Ride(enhanced: true));

        Assert.Equal(plain.Stream.AltitudeMeters, enhanced.Stream.AltitudeMeters);
        Assert.Equal(plain.Stream.SpeedMetersPerSecond, enhanced.Stream.SpeedMetersPerSecond);
        Assert.NotNull(enhanced.Stream.AltitudeMeters);
    }

    [Fact]
    public void RideWithoutSession_FallsBackToValuesComputedFromRecords()
    {
        var a = Parse(FitSampleBuilder.Ride(includeSession: false));

        Assert.Equal(Sport.Other, a.Sport); // senza sessione lo sport non e' noto
        Assert.Equal(Start, a.StartTime);
        Assert.Equal(60.0, a.ElapsedSeconds); // ultimo - primo istante
        Assert.Equal(60.0, a.MovingSeconds); // ogni intervallo a circa 8,5 m/s, sopra 0,5 m/s
        // Distanza: 60 passi da 912 semicerchi di latitudine su un meridiano (circa 510 m).
        Assert.Equal(54_720 * DegreesPerSemicircle * MetersPerDegree, a.DistanceMeters, 0.001);
        Assert.InRange(a.DistanceMeters, 509.9, 510.1);
        Assert.Equal(12.0, a.ElevationGainMeters); // 100 + 0,2 i: +0,2 m per 60 intervalli
        Assert.Equal(130.0, a.Summary.AvgHeartRate); // 100 + i, media su 61 record
        Assert.Equal(160.0, a.Summary.MaxHeartRate);
        Assert.Equal(210.0, a.Summary.AvgPowerWatts);
        Assert.Equal(90.0, a.Summary.AvgCadence);
        Assert.Equal(2, a.Laps.Count); // i giri non dipendono dalla sessione
    }

    [Fact]
    public void SwimWithoutGps_HasNullPositionColumns()
    {
        var a = Parse(FitSampleBuilder.Swim());

        Assert.Equal(Sport.Swim, a.Sport);
        Assert.Null(a.Stream.Latitude);
        Assert.Null(a.Stream.Longitude);
        Assert.Null(a.Stream.AltitudeMeters);
        Assert.Null(a.Stream.PowerWatts);
        Assert.Null(a.Stream.Cadence);
        Assert.NotNull(a.Stream.HeartRate);
        Assert.NotNull(a.Stream.SpeedMetersPerSecond);
        Assert.Null(a.ElevationGainMeters);
        Assert.Equal(60.0, a.ElapsedSeconds);
        Assert.Equal(60.0, a.MovingSeconds);
        Assert.Equal(60.0, a.DistanceMeters);
        Assert.Equal(120.0, a.Summary.AvgHeartRate);
        Assert.Null(a.Summary.AvgPowerWatts);
        Assert.Null(a.Summary.AvgCadence);
        Assert.Single(a.Laps);
    }

    [Fact]
    public void SwimWithoutSession_UsesCumulativeRecordDistance()
    {
        // Senza GPS ne' sessione la distanza si ricava dal campo "distance" cumulato dei record: 60 m.
        var msgs = new List<Mesg> { FitSampleBuilder.FileId() };
        for (var i = 0; i <= 60; i++)
        {
            var r = new RecordMesg();
            r.SetTimestamp(new FitDateTime(FitSampleBuilder.Start.AddSeconds(i)));
            r.SetDistance(1.0f * i);
            msgs.Add(r);
        }

        var a = Parse(FitSampleBuilder.Build(msgs));

        Assert.Equal(60.0, a.DistanceMeters);
        Assert.Equal(60.0, a.MovingSeconds); // velocita' dal distance cumulato: 1 m/s
        Assert.Null(a.Stream.HeartRate);
    }

    [Fact]
    public void MultiSession_BecomesOneOtherActivityWithSummedValues()
    {
        var a = Parse(FitSampleBuilder.MultiSession());

        Assert.Equal(Sport.Other, a.Sport);
        Assert.Equal(Start, a.StartTime);
        Assert.Equal(60.0, a.ElapsedSeconds); // 30 + 30
        Assert.Equal(58.0, a.MovingSeconds); // 28 + 30
        Assert.Equal(400.0, a.DistanceMeters); // 300 + 100
        Assert.Equal(8.0, a.ElevationGainMeters); // 5 + 3
        Assert.Equal(62, a.Stream.TimeOffsetSeconds.Count); // 31 + 31 record
        Assert.Equal(0.0, a.Stream.TimeOffsetSeconds[0]);
        Assert.Equal(30.0, a.Stream.TimeOffsetSeconds[30]);
        Assert.Equal(40.0, a.Stream.TimeOffsetSeconds[31]); // la corsa inizia 40 s dopo la bici
        Assert.Equal(70.0, a.Stream.TimeOffsetSeconds[61]);
        Assert.Equal(120.0, a.Summary.AvgHeartRate); // (31 * 100 + 31 * 140) / 62, dai record
        Assert.Equal(140.0, a.Summary.MaxHeartRate);
        Assert.Equal(2, a.Laps.Count);
        Assert.Equal(Start, a.Laps[0].StartTime);
        Assert.Equal(Start.AddSeconds(40), a.Laps[1].StartTime);
    }

    [Fact]
    public void LatitudeAndLongitude_AreRoundedToSevenDecimals()
    {
        // 2^29 + 1 semicerchi = 45,00000008381903... gradi: a 7 decimali 45,0000001.
        var r = FitSampleBuilder.RideRecord(0);
        r.SetPositionLat(FitSampleBuilder.Lat0Semicircles + 1);
        r.SetPositionLong(FitSampleBuilder.LonSemicircles - 1);
        var r2 = FitSampleBuilder.RideRecord(1);
        var bytes = FitSampleBuilder.Build([FitSampleBuilder.FileId(), r, r2]);

        var s = Parse(bytes).Stream;

        Assert.Equal(45.0000001, s.Latitude![0]);
        Assert.Equal(22.4999999, s.Longitude![0]);
    }

    [Fact]
    public void InvalidSentinels_AreReadAsMissingValues()
    {
        // I dispositivi scrivono il valore "non valido" del tipo base (0xFF, 0xFFFF, 0x7FFFFFFF...) per un campo
        // assente, e l'SDK lo restituisce come dato (la quota diventerebbe 12.607 m): il parser lo legge come
        // valore mancante. Il record centrale ha tutti i campi, ma con i valori non validi.
        // (Non si codificano record con campi mancanti dopo uno completo: il codificatore dell'SDK modifica stato
        // condiviso e altera le codifiche e le decodifiche successive nel processo.)
        var r0 = FitSampleBuilder.RideRecord(0);
        var r1 = FitSampleBuilder.RideRecord(1);
        r1.SetHeartRate(byte.MaxValue);
        r1.SetPower(ushort.MaxValue);
        r1.SetCadence(byte.MaxValue);
        r1.SetAltitude(12_607f); // (65535 / 5) - 500: grezzo 0xFFFF
        r1.SetSpeed(65.535f); // grezzo 0xFFFF a scala 1000
        r1.SetPositionLat(int.MaxValue);
        r1.SetPositionLong(int.MaxValue);
        var r2 = FitSampleBuilder.RideRecord(2);

        var s = Parse(FitSampleBuilder.Build([FitSampleBuilder.FileId(), r0, r1, r2])).Stream;

        Assert.Equal(new double?[] { 100, null, 102 }, s.HeartRate);
        Assert.Equal(new double?[] { 150, null, 154 }, s.PowerWatts);
        Assert.Equal(new double?[] { 90, null, 90 }, s.Cadence);
        Assert.Equal(new double?[] { 100.0, null, 100.4 }, s.AltitudeMeters);
        Assert.Equal(new double?[] { 8.5, null, 8.5 }, s.SpeedMetersPerSecond);
        Assert.NotNull(s.Latitude![0]);
        Assert.Null(s.Latitude[1]);
        Assert.Null(s.Longitude![1]);
        Assert.NotNull(s.Latitude[2]);
    }

    [Fact]
    public void NonSeekableStream_IsSupported()
    {
        var a = Parser.Parse(new ForwardOnlyStream(new MemoryStream(FitSampleBuilder.Ride())), CancellationToken.None);

        Assert.Equal(Sport.Ride, a.Sport);
        Assert.Equal(61, a.Stream.TimeOffsetSeconds.Count);
    }

    // ---- File non validi

    [Fact]
    public void TruncatedFile_IsRejected()
    {
        var bytes = FitSampleBuilder.Ride();

        Assert.Throws<ActivityFileException>(() => Parse(bytes[..(bytes.Length * 7 / 10)]));
        Assert.Throws<ActivityFileException>(() => Parse(bytes[..^1])); // manca solo un byte del CRC
        Assert.Throws<ActivityFileException>(() => Parse(bytes[..8])); // header incompleto
    }

    [Fact]
    public void CorruptedData_FailsTheIntegrityCheck()
    {
        var bytes = FitSampleBuilder.Ride();
        bytes[bytes.Length / 2] ^= 0xFF;

        Assert.Throws<ActivityFileException>(() => Parse(bytes));
    }

    [Fact]
    public void RandomBytes_AreRejected()
    {
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);

        Assert.Throws<ActivityFileException>(() => Parse(bytes));
    }

    [Fact]
    public void TextFileWithFitExtension_IsRejected()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("<gpx>questo non e' un FIT</gpx>");

        Assert.Throws<ActivityFileException>(() => Parse(bytes));
    }

    [Fact]
    public void EmptyStream_IsRejected()
    {
        Assert.Throws<ActivityFileException>(() => Parse([]));
    }

    [Fact]
    public void FileWithoutRecords_IsRejected()
    {
        var bytes = FitSampleBuilder.Build(
            [FitSampleBuilder.FileId(), FitSampleBuilder.Session(FitSport.Cycling, FitSampleBuilder.Start, 60, 60, 100)]);

        Assert.Throws<ActivityFileException>(() => Parse(bytes));
    }

    [Fact]
    public void ExceptionMessages_DoNotLeakFileContent()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("SEGRETO-FIT questo non e' un FIT");

        var ex = Assert.Throws<ActivityFileException>(() => Parse(bytes));

        Assert.DoesNotContain("SEGRETO-FIT", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData(FitSportCase.Running, Sport.Run)]
    [InlineData(FitSportCase.Cycling, Sport.Ride)]
    [InlineData(FitSportCase.Swimming, Sport.Swim)]
    [InlineData(FitSportCase.Walking, Sport.Other)]
    [InlineData(FitSportCase.Training, Sport.Other)]
    public void SportIsMappedFromTheSession(FitSportCase fit, Sport expected)
    {
        var sport = fit switch
        {
            FitSportCase.Running => FitSport.Running,
            FitSportCase.Cycling => FitSport.Cycling,
            FitSportCase.Swimming => FitSport.Swimming,
            FitSportCase.Walking => FitSport.Walking,
            _ => FitSport.Training,
        };
        var bytes = FitSampleBuilder.Build(
        [
            FitSampleBuilder.FileId(),
            FitSampleBuilder.RideRecord(0),
            FitSampleBuilder.RideRecord(1),
            FitSampleBuilder.Session(sport, FitSampleBuilder.Start, 1, 1, 8),
        ]);

        Assert.Equal(expected, Parse(bytes).Sport);
    }

    /// <summary>Sport FIT usati dal test, con valori serializzabili da xUnit.</summary>
    public enum FitSportCase
    {
        Running,
        Cycling,
        Swimming,
        Walking,
        Training,
    }
}
