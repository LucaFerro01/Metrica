using Metrica.Contracts;
using Metrica.Ingestion.Parsing;
using static Metrica.Ingestion.Tests.GpxBuilder;

namespace Metrica.Ingestion.Tests;

/// <summary>CanParse, invarianti delle colonne, arrotondamento (DEC-012), assenza di logging, uso concorrente.</summary>
[Trait("Category", "Parsing")]
public class ParserSupportTests
{
    private static readonly GpxParser Gpx = new();
    private static readonly FitParser Fit = new();

    // ---- CanParse

    [Theory]
    [InlineData("run.gpx", true)]
    [InlineData("RUN.GPX", true)]
    [InlineData("Run.Gpx", true)]
    [InlineData("dir/sub.dir/run.gpx", true)]
    [InlineData("run.fit", false)]
    [InlineData("run.gpx.txt", false)]
    [InlineData("run.gpxx", false)]
    [InlineData("gpx", false)]
    [InlineData(".txt", false)]
    [InlineData("", false)]
    public void GpxParser_CanParse_IsCaseInsensitiveOnTheExtension(string fileName, bool expected)
        => Assert.Equal(expected, Gpx.CanParse(fileName));

    [Theory]
    [InlineData("ride.fit", true)]
    [InlineData("RIDE.FIT", true)]
    [InlineData("Ride.Fit", true)]
    [InlineData("dir/sub.dir/ride.fit", true)]
    [InlineData("ride.gpx", false)]
    [InlineData("ride.fit.bak", false)]
    [InlineData("ride.fitx", false)]
    [InlineData("fit", false)]
    [InlineData("", false)]
    public void FitParser_CanParse_IsCaseInsensitiveOnTheExtension(string fileName, bool expected)
        => Assert.Equal(expected, Fit.CanParse(fileName));

    // ---- Invarianti dello stream su tutti gli esempi

    public static TheoryData<string> AllSamples =>
    [
        "gpx:run.gpx",
        "gpx:gpx-no-elevation.gpx",
        "fit:ride.fit",
        "fitb:ride",
        "fitb:ride-no-session",
        "fitb:swim",
        "fitb:multi",
    ];

    private static ParsedActivity Load(string key)
    {
        var colon = key.IndexOf(':');
        var kind = key[..colon];
        var name = key[(colon + 1)..];
        switch (kind)
        {
            case "gpx":
                using (var s = SampleFiles.Open(name))
                {
                    return Gpx.Parse(s, CancellationToken.None);
                }

            case "fit":
                using (var s = SampleFiles.Open(name))
                {
                    return Fit.Parse(s, CancellationToken.None);
                }

            default:
                var bytes = name switch
                {
                    "ride" => FitSampleBuilder.Ride(),
                    "ride-no-session" => FitSampleBuilder.Ride(includeSession: false),
                    "swim" => FitSampleBuilder.Swim(),
                    _ => FitSampleBuilder.MultiSession(),
                };
                return Fit.Parse(new MemoryStream(bytes), CancellationToken.None);
        }
    }

    private static IEnumerable<(string Name, IReadOnlyList<double?>? Column)> OptionalColumns(StreamColumns s) =>
    [
        (nameof(s.Latitude), s.Latitude),
        (nameof(s.Longitude), s.Longitude),
        (nameof(s.AltitudeMeters), s.AltitudeMeters),
        (nameof(s.HeartRate), s.HeartRate),
        (nameof(s.PowerWatts), s.PowerWatts),
        (nameof(s.Cadence), s.Cadence),
        (nameof(s.SpeedMetersPerSecond), s.SpeedMetersPerSecond),
    ];

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Columns_HaveTheSameLength_AndOffsetsAreNonNegativeAndNonDecreasing(string key)
    {
        var s = Load(key).Stream;
        var n = s.TimeOffsetSeconds.Count;

        Assert.True(n > 0);
        foreach (var (name, column) in OptionalColumns(s))
        {
            if (column is not null)
            {
                Assert.True(column.Count == n, $"{name}: {column.Count} valori invece di {n}");
                Assert.Contains(column, v => v.HasValue); // una colonna presente ha almeno un valore, altrimenti e' null
            }
        }

        Assert.True(s.TimeOffsetSeconds[0] >= 0);
        for (var i = 1; i < n; i++)
        {
            Assert.True(s.TimeOffsetSeconds[i] >= s.TimeOffsetSeconds[i - 1], $"offset decrescente al punto {i}");
        }
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void SamplesWithoutRepeatedInstants_HaveStrictlyIncreasingOffsets(string key)
    {
        // Punti con lo stesso istante restano (offset non decrescenti, vedi RepeatedTimestamps...), ma nessun campione ne ha.
        var offsets = Load(key).Stream.TimeOffsetSeconds;

        for (var i = 1; i < offsets.Count; i++)
        {
            Assert.True(offsets[i] > offsets[i - 1], $"offset non strettamente crescente al punto {i}");
        }
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void EveryParsedActivity_SerializesWithTheContractOptions(string key)
    {
        var json = Serialization.ToEnvelopeJson(Load(key));

        Assert.NotEmpty(json);
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Streams_AreRoundedAccordingToDec012(string key)
    {
        AssertRounded(Load(key).Stream);
    }

    private static void AssertRounded(StreamColumns s)
    {
        foreach (var v in s.TimeOffsetSeconds)
        {
            Assert.Equal(Math.Round(v, 3), v);
        }

        void Check(IReadOnlyList<double?>? column, int decimals, string name)
        {
            foreach (var v in column ?? [])
            {
                if (v is { } d)
                {
                    Assert.True(Math.Round(d, decimals) == d, $"{name} = {d:R} non e' arrotondato a {decimals} decimali");
                }
            }
        }

        Check(s.Latitude, 7, nameof(s.Latitude));
        Check(s.Longitude, 7, nameof(s.Longitude));
        Check(s.AltitudeMeters, 1, nameof(s.AltitudeMeters));
        Check(s.SpeedMetersPerSecond, 3, nameof(s.SpeedMetersPerSecond));
        Check(s.HeartRate, 0, nameof(s.HeartRate));
        Check(s.PowerWatts, 0, nameof(s.PowerWatts));
        Check(s.Cadence, 0, nameof(s.Cadence));
    }

    // ---- Arrotondamento DEC-012 su valori con molte cifre in ingresso

    [Fact]
    public void Gpx_InputWithManyDecimals_IsRounded()
    {
        static string Ext(string hr, string cad, string power, string speed) =>
            $"<extensions><power>{power}</power><gpxtpx:TrackPointExtension><gpxtpx:hr>{hr}</gpxtpx:hr><gpxtpx:cad>{cad}</gpxtpx:cad><gpxtpx:speed>{speed}</gpxtpx:speed></gpxtpx:TrackPointExtension></extensions>";

        var xml = Doc(Trk("running", Seg(
            Pt("45.123456789012", "-7.987654321098", T(0), "123.456", Ext("151.6", "89.6", "249.4", "3.14159265")),
            Pt("-33.86881974999", "151.20929550001", T(1.23456), "123.449", Ext("140.4", "80.4", "250.6", "0.0004")),
            Pt("45.0", "7.5", T(2.0004), "99.94", Ext("100", "90", "300", "12.3456")))));

        var s = Gpx.Parse(Bytes(xml), CancellationToken.None).Stream;

        Assert.Equal(new double[] { 0.0, 1.235, 2.0 }, s.TimeOffsetSeconds); // 1,23456 -> 1,235; 2,0004 -> 2,0
        Assert.Equal(new double?[] { 45.1234568, -33.8688197, 45.0 }, s.Latitude); // 7 decimali
        Assert.Equal(new double?[] { -7.9876543, 151.2092955, 7.5 }, s.Longitude);
        Assert.Equal(new double?[] { 123.5, 123.4, 99.9 }, s.AltitudeMeters); // 0,1 m
        Assert.Equal(new double?[] { 3.142, 0.0, 12.346 }, s.SpeedMetersPerSecond); // 0,001 m/s
        Assert.Equal(new double?[] { 152, 140, 100 }, s.HeartRate); // interi
        Assert.Equal(new double?[] { 249, 251, 300 }, s.PowerWatts);
        Assert.Equal(new double?[] { 90, 80, 90 }, s.Cadence);
        AssertRounded(s);
    }

    [Fact]
    public void Gpx_RoundingNeverProducesNegativeZero()
    {
        var xml = Doc(Trk("running", Seg(
            Pt("-0.00000001", "-0.00000002", T(0), "-0.04"),
            Pt("0.0", "0.0", T(1), "0.0"))));

        var s = Gpx.Parse(Bytes(xml), CancellationToken.None).Stream;

        Assert.False(double.IsNegative(s.Latitude![0]!.Value));
        Assert.False(double.IsNegative(s.Longitude![0]!.Value));
        Assert.False(double.IsNegative(s.AltitudeMeters![0]!.Value));
    }

    [Fact]
    public void Fit_ValuesAreRoundedEvenWhenTheSdkReturnsFloats()
    {
        // La velocita' FIT e' un float: 3.3f vale 3,2999999523162842 come double e deve uscire 3,3.
        var r0 = FitSampleBuilder.RideRecord(0);
        r0.SetSpeed(3.3f);
        var r1 = FitSampleBuilder.RideRecord(1);
        r1.SetSpeed(0.0004f);

        var s = Fit.Parse(
            new MemoryStream(FitSampleBuilder.Build([FitSampleBuilder.FileId(), r0, r1])),
            CancellationToken.None).Stream;

        Assert.Equal(3.3, s.SpeedMetersPerSecond![0]);
        AssertRounded(s);
    }

    // ---- Nessun logging

    [Fact]
    public void ParserCode_DoesNotUseLogging()
    {
        // Da T10 l'assembly contiene anche l'endpoint di upload, che registra solo l'esito (REQ-034) e quindi usa
        // ILogger: il divieto vale per il CODICE DEI PARSER (nessun log di dati del file), non piu' per l'intero assembly.
        var references = typeof(GpxParser).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(references, n => n.Contains("Serilog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, n => n.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase));

        var parserFiles = Directory.GetFiles(Path.Combine(ServicePaths.ProjectDirectory, "Parsing"), "*.cs")
            .Concat(new[] { "IActivityFileParser.cs", "ActivityFileException.cs", "ParsedActivity.cs" }.Select(f => Path.Combine(ServicePaths.ProjectDirectory, f)))
            .ToList();
        Assert.True(parserFiles.Count >= 8, "i file dei parser devono essere trovati");
        foreach (var file in parserFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "ILogger", "Logging", "LogInformation", "LogWarning", "LogError", "LogDebug", "Serilog", "OpenTelemetry", "Console.Write", "Console.Error", "Console.Out", "Debug.Write", "Trace.Write" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} usa '{forbidden}': i parser non scrivono log.");
            }
        }

        // Anche nelle firme: nessun campo, costruttore o metodo dei tipi dei parser dipende da tipi di logging.
        var parserTypes = typeof(GpxParser).Assembly.GetTypes()
            .Where(t => t.Namespace == "Metrica.Ingestion.Parsing" || t == typeof(ParsedActivity) || t == typeof(ActivityFileException));
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var type in parserTypes)
        {
            var used = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(x => x.PropertyType))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(x => x.ParameterType)))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(x => x.ParameterType).Append(m.ReturnType)));
            Assert.DoesNotContain(used, t => (t.Namespace ?? "").StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal));
        }
    }

    // ---- Uso concorrente (T10 puo' registrare i parser come singleton)

    [Fact]
    public void Parsers_AreSafeForConcurrentUse()
    {
        var gpxBytes = File.ReadAllBytes(SampleFiles.PathOf("run.gpx"));
        var fitBytes = FitSampleBuilder.Ride();

        var results = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(8)
            .Select(i => i % 2 == 0
                ? Gpx.Parse(new MemoryStream(gpxBytes), CancellationToken.None).DistanceMeters
                : Fit.Parse(new MemoryStream(fitBytes), CancellationToken.None).DistanceMeters)
            .ToList();

        Assert.Equal(8, results.Count(d => d == 510.0));
        Assert.Equal(8, results.Count(d => Math.Abs(d - 166.7924) < 0.01));
    }
}
