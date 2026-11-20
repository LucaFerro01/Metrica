using Dynastream.Fit;
using Metrica.IntegrationTests.Support;
using FitDateTime = Dynastream.Fit.DateTime;
using SysDateTime = System.DateTime;

namespace Metrica.IntegrationTests.Files;

/// <summary>
/// Uscita in bici in formato FIT, codificata con il codificatore del SDK Garmin (come il generatore dei test di ingestion) con
/// valori NOTI per il calcolo a mano: record a 1 Hz (i = 0..N-1), frequenza cardiaca 100 + i % 100, potenza 150 + 2 i, cadenza
/// 90, velocita' 8,5 m/s (distanza cumulata 8,5 i), quota 100 + 0,2 i, latitudine +912 semicerchi per record. Due giri e una
/// sessione coerenti con i record. Inizio e posizione dipendono dal seme: ogni test ha un file (e un hash) proprio.
/// </summary>
internal sealed class FitRide
{
    private const double SemicirclesToDegrees = 180.0 / 2_147_483_648.0;
    private const int LatStepSemicircles = 912;

    public required Guid Seed { get; init; }

    public int Records { get; init; } = 61;

    public int? ConstantHeartRate { get; init; }

    public int? ConstantPower { get; init; }

    public string DefaultFileName => $"uscita-privata-{Seed:N}.fit";

    public SysDateTime Start => ComputeStart(Seed);

    private int Lat0Semicircles => 536_870_912 + (int)(BitConverter.ToUInt32(Seed.ToByteArray(), 0) % 100_000_000);

    private int LonSemicircles => 268_435_456 + (int)(BitConverter.ToUInt32(Seed.ToByteArray(), 4) % 100_000_000);

    public int HeartRate(int i) => ConstantHeartRate ?? 100 + i % 100;

    public int Power(int i) => ConstantPower ?? 150 + 2 * i;

    public double Latitude(int i) => (Lat0Semicircles + (long)LatStepSemicircles * i) * SemicirclesToDegrees;

    public double Longitude => LonSemicircles * SemicirclesToDegrees;

    // ---- Valori attesi dal file ---------------------------------------------------------------------------------------

    public double ExpectedDistanceMeters => 8.5 * (Records - 1);

    public double ExpectedElapsedSeconds => Records - 1;

    public double ExpectedElevationGainMeters => Math.Round(0.2 * (Records - 1), 1);

    public double ExpectedAverageHeartRate => Enumerable.Range(0, Records).Average(HeartRate);

    public double ExpectedMaxHeartRate => Enumerable.Range(0, Records).Max(HeartRate);

    public double ExpectedAveragePower => Enumerable.Range(0, Records).Average(Power);

    public double ExpectedAverageCadence => 90;

    /// <summary>Due giri: i primi <c>N/2</c> secondi e il resto (durata, distanza).</summary>
    public IReadOnlyList<(double ElapsedSeconds, double DistanceMeters)> ExpectedLaps =>
        [(Records / 2, 8.5 * (Records / 2)), (Records - 1 - Records / 2, 8.5 * (Records - 1 - Records / 2))];

    public GeneratedFile Build(string? fileName = null)
    {
        var name = fileName ?? DefaultFileName;
        var probes = new PrivacyProbes();
        foreach (var i in new[] { 0, 1, Records / 2, Records - 1 })
        {
            probes.AddCoordinate("latitudine", Latitude(i));
        }

        probes.AddCoordinate("longitudine", Longitude)
            .AddText("nome del file", name)
            .AddText("nome del file senza estensione", Path.GetFileNameWithoutExtension(name))
            .AddEventBody();
        if (ConstantHeartRate is { } hr)
        {
            probes.AddNumber("frequenza cardiaca", hr);
        }

        if (ConstantPower is { } power)
        {
            probes.AddNumber("potenza", power);
        }

        return new GeneratedFile(name, Encode(), probes);
    }

    private byte[] Encode()
    {
        var messages = new List<Mesg> { FileId() };
        var half = Records / 2;
        for (var i = 0; i < Records; i++)
        {
            messages.Add(Record(i));
            if (i == half)
            {
                messages.Add(Lap(Start, half, 8.5f * half));
            }
        }

        messages.Add(Lap(Start.AddSeconds(half), Records - 1 - half, 8.5f * (Records - 1 - half)));
        messages.Add(Session());

        var encoder = new Encode(ProtocolVersion.V20);
        using var stream = new MemoryStream();
        encoder.Open(stream);
        encoder.Write(messages);
        encoder.Close();
        return stream.ToArray();
    }

    private FileIdMesg FileId()
    {
        var m = new FileIdMesg();
        m.SetType(Dynastream.Fit.File.Activity);
        m.SetManufacturer(Manufacturer.Development);
        m.SetProduct(1);
        m.SetSerialNumber(1234);
        m.SetTimeCreated(new FitDateTime(Start));
        return m;
    }

    private RecordMesg Record(int i)
    {
        var m = new RecordMesg();
        m.SetTimestamp(new FitDateTime(Start.AddSeconds(i)));
        m.SetPositionLat(Lat0Semicircles + LatStepSemicircles * i);
        m.SetPositionLong(LonSemicircles);
        m.SetAltitude(100f + 0.2f * i);
        m.SetHeartRate((byte)HeartRate(i));
        m.SetPower((ushort)Power(i));
        m.SetCadence(90);
        m.SetSpeed(8.5f);
        m.SetDistance(8.5f * i);
        return m;
    }

    private static LapMesg Lap(SysDateTime start, float elapsed, float distance)
    {
        var m = new LapMesg();
        m.SetStartTime(new FitDateTime(start));
        m.SetTimestamp(new FitDateTime(start.AddSeconds(elapsed)));
        m.SetTotalElapsedTime(elapsed);
        m.SetTotalTimerTime(elapsed);
        m.SetTotalDistance(distance);
        return m;
    }

    private SessionMesg Session()
    {
        var m = new SessionMesg();
        m.SetSport(Sport.Cycling);
        m.SetStartTime(new FitDateTime(Start));
        m.SetTimestamp(new FitDateTime(Start.AddSeconds(Records - 1)));
        m.SetTotalElapsedTime(Records - 1);
        m.SetTotalTimerTime(Records - 1);
        m.SetTotalDistance((float)ExpectedDistanceMeters);
        m.SetAvgHeartRate((byte)Math.Round(ExpectedAverageHeartRate));
        m.SetMaxHeartRate((byte)ExpectedMaxHeartRate);
        m.SetAvgPower((ushort)Math.Round(ExpectedAveragePower));
        m.SetAvgCadence(90);
        m.SetTotalAscent((ushort)Math.Round(ExpectedElevationGainMeters));
        return m;
    }

    private static SysDateTime ComputeStart(Guid seed)
    {
        var bytes = seed.ToByteArray();
        var days = BitConverter.ToUInt32(bytes, 8) % 2000;
        var seconds = BitConverter.ToUInt32(bytes, 12) % 86_400;
        return new SysDateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(days).AddSeconds(seconds);
    }
}
