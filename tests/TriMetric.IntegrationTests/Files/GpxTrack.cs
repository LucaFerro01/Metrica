using System.Globalization;
using System.Text;
using TriMetric.IntegrationTests.Support;

namespace TriMetric.IntegrationTests.Files;

[Flags]
internal enum GpxColumns
{
    None = 0,
    Altitude = 1,
    HeartRate = 2,
    Power = 4,
    Cadence = 8,
    Speed = 16,
    All = Altitude | HeartRate | Power | Cadence | Speed,
}

/// <summary>
/// Una corsa generata in memoria con geometria e valori NOTI: il test ne calcola da se' i valori attesi (distanza, durata,
/// dislivello, medie, giri) senza guardare l'output del sistema.
/// </summary>
/// <remarks>
/// <para>La traccia sale lungo un meridiano di 0,000027 gradi per punto (3,0027 m con il raggio terrestre medio di 6.371.000 m;
/// la distanza attesa e' quindi in forma chiusa). Latitudine e longitudine sono costruite in interi (unita' di 1e-7 gradi, o
/// 1e-9 con <see cref="OverPrecise"/>), cosi il file contiene esattamente i decimali voluti. Quota: onda triangolare di
/// 10 m ogni 100 punti (10 m di salita per ciclo). Frequenza cardiaca, potenza, cadenza e velocita' variano con l'indice, o
/// sono costanti se richiesto (valori distintivi per i controlli sui log).</para>
/// <para>Con <see cref="OverPrecise"/> coordinate, quota e velocita' hanno piu' decimali di quelli che l'evento puo' portare
/// (9 per le coordinate, 2 per la quota, 6 per la velocita'): i valori nel database devono essere quelli ARROTONDATI (DEC-012).</para>
/// </remarks>
internal sealed class GpxTrack
{
    /// <summary>Passo in latitudine per punto, in unita' di 1e-7 gradi (0,000027 gradi).</summary>
    public const int LatStepE7 = 270;

    /// <summary>Raggio terrestre medio usato dai test (indipendente da quello del servizio, 6.371.008,8 m).</summary>
    public const double EarthRadiusMeters = 6_371_000;

    public required Guid Seed { get; init; }

    public int Points { get; init; } = 600;

    public DateTimeOffset Start { get; init; }

    public int StepMilliseconds { get; init; } = 1000;

    /// <summary>Numero di <c>trkseg</c>; dal secondo in poi i punti sono preceduti da <see cref="PauseSeconds"/> di pausa.</summary>
    public int Segments { get; init; } = 1;

    public int PauseSeconds { get; init; }

    public GpxColumns Columns { get; init; } = GpxColumns.All;

    public int? ConstantHeartRate { get; init; }

    public int? ConstantPower { get; init; }

    public bool OverPrecise { get; init; }

    /// <summary>Contenuto in piu' in un frammento distintivo (nome della traccia), per i controlli sulla privacy.</summary>
    public string NameMarker => $"percorso-riservato-{Seed:N}";

    public string CreatorMarker => $"creatore-riservato-{Seed:N}";

    public string DefaultFileName => $"giro-privato-{Seed:N}.gpx";

    /// <summary>Latitudine del primo punto, in unita' di 1e-7 gradi (30..70 gradi, derivata dal seme).</summary>
    public long Lat0E7 => 300_000_000 + Part(0) % 400_000_000;

    /// <summary>Longitudine, costante, in unita' di 1e-7 gradi (1..81 gradi).</summary>
    public long Lon0E7 => 10_000_000 + Part(1) % 800_000_000;

    /// <summary>Un inizio qualsiasi dal 2020 al 2025 derivato dal seme (a meno che il test lo fissi).</summary>
    public static DateTimeOffset StartFromSeed(Guid seed)
    {
        var bytes = seed.ToByteArray();
        var days = BitConverter.ToUInt32(bytes, 8) % 2000;
        var seconds = BitConverter.ToUInt32(bytes, 12) % 86_400;
        return new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(days).AddSeconds(seconds);
    }

    // ---- Valori del punto i, come scritti nel file ---------------------------------------------------------------------

    /// <summary>Segmento (0-based) del punto <paramref name="i"/>.</summary>
    public int SegmentOf(int i) => (int)((long)i * Segments / Points);

    /// <summary>Primo indice del segmento <paramref name="segment"/>.</summary>
    public int SegmentStart(int segment) => (int)(((long)segment * Points + Segments - 1) / Segments);

    /// <summary>Istante del punto <paramref name="i"/> in millisecondi dall'inizio.</summary>
    public long OffsetMs(int i) => (long)i * StepMilliseconds + (long)SegmentOf(i) * PauseSeconds * 1000;

    public DateTimeOffset TimeOf(int i) => Start.AddMilliseconds(OffsetMs(i));

    /// <summary>Latitudine nel file in unita' di 1e-9 gradi (con <see cref="OverPrecise"/> due decimali in piu', mai a meta' strada).</summary>
    public long LatitudeE9(int i) => (Lat0E7 + (long)i * LatStepE7) * 100 + (OverPrecise ? new[] { 13L, 87, 26, 74 }[i % 4] : 0);

    public long LongitudeE9(int i) => Lon0E7 * 100 + (OverPrecise ? new[] { 74L, 26, 87, 13 }[i % 4] : 0);

    /// <summary>Latitudine che l'evento deve portare: quella del file arrotondata a 7 decimali (DEC-012).</summary>
    public double ExpectedLatitude(int i) => (double)RoundE9ToE7(LatitudeE9(i)) / 10_000_000;

    public double ExpectedLongitude(int i) => (double)RoundE9ToE7(LongitudeE9(i)) / 10_000_000;

    /// <summary>Quota nel file in millimetri.</summary>
    public long AltitudeMm(int i)
    {
        var cycle = i % 100;
        var triangle = cycle < 50 ? cycle : 100 - cycle;
        return 100_000 + triangle * 200L + (OverPrecise ? (i % 2 == 0 ? 40 : 60) : 0);
    }

    /// <summary>Quota che l'evento deve portare: arrotondata a 0,1 m.</summary>
    public double ExpectedAltitude(int i) => (double)Math.Round(AltitudeMm(i) / 1000m, 1, MidpointRounding.AwayFromZero);

    public int HeartRate(int i) => ConstantHeartRate ?? 100 + i % 80;

    public int Power(int i) => ConstantPower ?? 200 + i % 50;

    public int Cadence(int i) => 80 + i % 10;

    /// <summary>Velocita' nel file in micrometri al secondo.</summary>
    public long SpeedMicro(int i) => (3000 + i % 1000) * 1000L + (OverPrecise ? 123 : 0);

    public double ExpectedSpeed(int i) => (3000 + i % 1000) / 1000.0;

    // ---- Valori attesi (calcolati dal file, non dall'output del sistema) ------------------------------------------------

    public DateTimeOffset ExpectedStart => Start;

    public double ExpectedElapsedSeconds => OffsetMs(Points - 1) / 1000.0;

    /// <summary>Distanza attesa: (N-1) passi di 0,000027 gradi lungo un meridiano.</summary>
    public double ExpectedDistanceMeters => (Points - 1) * StepMeters;

    public static double StepMeters => LatStepE7 * 1e-7 * Math.PI / 180 * EarthRadiusMeters;

    /// <summary>Tempo in movimento: gli intervalli con velocita' di almeno 0,5 m/s (sono esclusi quelli che contengono una pausa).</summary>
    public double ExpectedMovingSeconds
    {
        get
        {
            double moving = 0;
            for (var i = 1; i < Points; i++)
            {
                var seconds = (OffsetMs(i) - OffsetMs(i - 1)) / 1000.0;
                if (StepMeters / seconds >= 0.5)
                {
                    moving += seconds;
                }
            }

            return moving;
        }
    }

    /// <summary>Somma delle salite tra quote consecutive (valori del file, senza arrotondamento).</summary>
    public double ExpectedElevationGainMeters
    {
        get
        {
            long gain = 0;
            for (var i = 1; i < Points; i++)
            {
                gain += Math.Max(0, AltitudeMm(i) - AltitudeMm(i - 1));
            }

            return gain / 1000.0;
        }
    }

    public double ExpectedAverageHeartRate => Enumerable.Range(0, Points).Average(HeartRate);

    public double ExpectedAveragePower => Enumerable.Range(0, Points).Average(Power);

    public double ExpectedAverageCadence => Enumerable.Range(0, Points).Average(Cadence);

    /// <summary>Un giro per segmento quando i segmenti sono piu' di uno: (inizio, durata, distanza).</summary>
    public IReadOnlyList<(DateTimeOffset Start, double ElapsedSeconds, double DistanceMeters)> ExpectedLaps =>
        Segments < 2
            ? []
            : [.. Enumerable.Range(0, Segments).Select(k =>
            {
                var first = SegmentStart(k);
                var last = SegmentStart(k + 1) - 1;
                return (TimeOf(first), (OffsetMs(last) - OffsetMs(first)) / 1000.0, (last - first) * StepMeters);
            })];

    // ---- Generazione -----------------------------------------------------------------------------------------------------

    public GeneratedFile Build(string? fileName = null)
    {
        var name = fileName ?? DefaultFileName;
        return new GeneratedFile(name, Render(), Probes(name));
    }

    /// <summary>Un campione di coordinate (primo, ultimo, intermedi) e, se distintivi, FC e potenza, piu' i frammenti del file.</summary>
    public PrivacyProbes Probes(string fileName)
    {
        var probes = new PrivacyProbes();
        var indices = new SortedSet<int> { 0, 1, Points / 2, Points - 1 };
        for (var k = 1; k < 20; k++)
        {
            indices.Add((int)((long)k * (Points - 1) / 20));
        }

        foreach (var i in indices)
        {
            probes.AddCoordinate("latitudine", ExpectedLatitude(i));
            probes.AddCoordinate("longitudine", ExpectedLongitude(i));
        }

        probes.AddText("nome del file", fileName)
            .AddText("nome del file senza estensione", Path.GetFileNameWithoutExtension(fileName))
            .AddText("nome della traccia", NameMarker)
            .AddText("creatore del file", CreatorMarker)
            .AddText("contenuto GPX", "<trkpt")
            .AddText("contenuto GPX", "TrackPointExtension");
        if (ConstantHeartRate is { } hr)
        {
            probes.AddNumber("frequenza cardiaca", hr);
        }

        if (ConstantPower is { } power)
        {
            probes.AddNumber("potenza", power);
        }

        return probes.AddEventBody();
    }

    private byte[] Render()
    {
        var bytes = new MemoryStream(Points * (OverPrecise ? 300 : 280) + 4096);
        using (var writer = new StreamWriter(bytes, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
        {
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            writer.Write($"<gpx version=\"1.1\" creator=\"{CreatorMarker}\" xmlns=\"http://www.topografix.com/GPX/1/1\" xmlns:gpxtpx=\"http://www.garmin.com/xmlschemas/TrackPointExtension/v1\">");
            writer.Write($"<trk><name>{NameMarker}</name><type>running</type>");
            for (var i = 0; i < Points; i++)
            {
                if (i == 0 || SegmentOf(i) != SegmentOf(i - 1))
                {
                    writer.Write(i == 0 ? "<trkseg>" : "</trkseg><trkseg>");
                }

                WritePoint(writer, i);
            }

            writer.Write("</trkseg></trk></gpx>");
        }

        return bytes.ToArray();
    }

    private void WritePoint(StreamWriter w, int i)
    {
        w.Write("<trkpt lat=\"");
        WriteScaled(w, LatitudeE9(i), 9, OverPrecise ? 9 : 7);
        w.Write("\" lon=\"");
        WriteScaled(w, LongitudeE9(i), 9, OverPrecise ? 9 : 7);
        w.Write("\">");
        if (Columns.HasFlag(GpxColumns.Altitude))
        {
            w.Write("<ele>");
            WriteScaled(w, AltitudeMm(i), 3, OverPrecise ? 3 : 1);
            w.Write("</ele>");
        }

        w.Write("<time>");
        w.Write(TimeOf(i).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        w.Write("</time>");

        var extensions = new StringBuilder();
        if (Columns.HasFlag(GpxColumns.Power))
        {
            extensions.Append("<power>").Append(Power(i).ToString(CultureInfo.InvariantCulture)).Append("</power>");
        }

        var tpx = new StringBuilder();
        if (Columns.HasFlag(GpxColumns.HeartRate))
        {
            tpx.Append("<gpxtpx:hr>").Append(HeartRate(i).ToString(CultureInfo.InvariantCulture)).Append("</gpxtpx:hr>");
        }

        if (Columns.HasFlag(GpxColumns.Cadence))
        {
            tpx.Append("<gpxtpx:cad>").Append(Cadence(i).ToString(CultureInfo.InvariantCulture)).Append("</gpxtpx:cad>");
        }

        if (Columns.HasFlag(GpxColumns.Speed))
        {
            var micro = SpeedMicro(i);
            tpx.Append("<gpxtpx:speed>").Append((micro / 1_000_000).ToString(CultureInfo.InvariantCulture)).Append('.')
                .Append((micro % 1_000_000).ToString("D6", CultureInfo.InvariantCulture)[..(OverPrecise ? 6 : 3)]).Append("</gpxtpx:speed>");
        }

        if (tpx.Length > 0)
        {
            extensions.Append("<gpxtpx:TrackPointExtension>").Append(tpx).Append("</gpxtpx:TrackPointExtension>");
        }

        if (extensions.Length > 0)
        {
            w.Write("<extensions>");
            w.Write(extensions.ToString());
            w.Write("</extensions>");
        }

        w.Write("</trkpt>");
    }

    /// <summary>Scrive <paramref name="value"/> / 10^decimals con esattamente quei decimali (interi: nessun errore di virgola mobile).</summary>
    private static void WriteScaled(StreamWriter w, long value, int decimals, int? trimToDecimals = null)
    {
        var scale = (long)Math.Pow(10, decimals);
        var whole = value / scale;
        var fraction = (value % scale).ToString("D" + decimals, CultureInfo.InvariantCulture);
        w.Write(whole.ToString(CultureInfo.InvariantCulture));
        w.Write('.');
        w.Write(trimToDecimals is { } keep ? fraction[..keep] : fraction);
    }

    private static long RoundE9ToE7(long e9)
    {
        var remainder = e9 % 100;
        return e9 / 100 + (remainder >= 50 ? 1 : 0);
    }

    private uint Part(int index) => BitConverter.ToUInt32(Seed.ToByteArray(), index * 4);
}
