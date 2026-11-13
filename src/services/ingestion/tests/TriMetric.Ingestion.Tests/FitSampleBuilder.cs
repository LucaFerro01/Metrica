using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;
using FitSport = Dynastream.Fit.Sport;
using SysDateTime = System.DateTime;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// Generatore deterministico di file FIT per i test, basato sul codificatore del SDK Garmin
/// (<c>Encode</c> con <c>FileIdMesg</c>, <c>RecordMesg</c>, <c>LapMesg</c>, <c>SessionMesg</c>).
/// Gli stessi byte escono a ogni esecuzione: <c>Samples/ride.fit</c> e' l'output di <see cref="Ride"/>
/// (un test lo verifica byte per byte).
/// </summary>
/// <remarks>
/// Valori scelti per il calcolo a mano (record a 1 Hz da <see cref="Start"/>, indice i = 0..60):
/// <list type="bullet">
/// <item>posizione: latitudine 2^29 semicerchi (45,0 gradi) piu' 912 semicerchi per record, longitudine 2^28 (22,5 gradi);</item>
/// <item>quota 100 + 0,2 i metri; frequenza cardiaca 100 + i; potenza 150 + 2 i; cadenza 90; velocita' 8,5 m/s;</item>
/// <item>distanza cumulata del record: 8,5 i metri.</item>
/// </list>
/// </remarks>
internal static class FitSampleBuilder
{
    public static readonly SysDateTime Start = new(2024, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Semicerchi di latitudine del record 0: 2^29 = 45 gradi.</summary>
    public const int Lat0Semicircles = 536_870_912;

    /// <summary>Semicerchi di longitudine di tutti i record: 2^28 = 22,5 gradi.</summary>
    public const int LonSemicircles = 268_435_456;

    /// <summary>Incremento di latitudine per record, in semicerchi.</summary>
    public const int LatStepSemicircles = 912;

    public static byte[] Build(IEnumerable<Mesg> messages)
    {
        var encoder = new Encode(ProtocolVersion.V20);
        using var ms = new MemoryStream();
        encoder.Open(ms);
        encoder.Write(messages);
        encoder.Close();
        return ms.ToArray();
    }

    public static FileIdMesg FileId()
    {
        var m = new FileIdMesg();
        m.SetType(Dynastream.Fit.File.Activity);
        m.SetManufacturer(Manufacturer.Development);
        m.SetProduct(1);
        m.SetSerialNumber(1234);
        m.SetTimeCreated(new FitDateTime(Start));
        return m;
    }

    /// <summary>Un record della bici di esempio; i campi non richiesti restano assenti.</summary>
    public static RecordMesg RideRecord(
        int i, SysDateTime? start = null, bool gps = true, bool enhanced = false, bool altitude = true,
        bool heartRate = true, bool power = true, bool cadence = true, bool speed = true)
    {
        var m = new RecordMesg();
        m.SetTimestamp(new FitDateTime((start ?? Start).AddSeconds(i)));
        if (gps)
        {
            m.SetPositionLat(Lat0Semicircles + LatStepSemicircles * i);
            m.SetPositionLong(LonSemicircles);
        }

        if (altitude)
        {
            var alt = 100f + 0.2f * i;
            if (enhanced)
            {
                m.SetEnhancedAltitude(alt);
            }
            else
            {
                m.SetAltitude(alt);
            }
        }

        if (heartRate)
        {
            m.SetHeartRate((byte)(100 + i % 100));
        }

        if (power)
        {
            m.SetPower((ushort)(150 + 2 * i));
        }

        if (cadence)
        {
            m.SetCadence(90);
        }

        if (speed)
        {
            if (enhanced)
            {
                m.SetEnhancedSpeed(8.5f);
            }
            else
            {
                m.SetSpeed(8.5f);
            }

            m.SetDistance(8.5f * i);
        }

        return m;
    }

    public static LapMesg Lap(SysDateTime start, float elapsed, float distance)
    {
        var m = new LapMesg();
        m.SetStartTime(new FitDateTime(start));
        m.SetTimestamp(new FitDateTime(start.AddSeconds(elapsed)));
        m.SetTotalElapsedTime(elapsed);
        m.SetTotalTimerTime(elapsed);
        m.SetTotalDistance(distance);
        return m;
    }

    public static SessionMesg Session(
        FitSport sport, SysDateTime start, float elapsed, float timer, float distance,
        byte? avgHr = null, byte? maxHr = null, ushort? avgPower = null, byte? avgCadence = null, ushort? ascent = null)
    {
        var m = new SessionMesg();
        m.SetSport(sport);
        m.SetStartTime(new FitDateTime(start));
        m.SetTimestamp(new FitDateTime(start.AddSeconds(elapsed)));
        m.SetTotalElapsedTime(elapsed);
        m.SetTotalTimerTime(timer);
        m.SetTotalDistance(distance);

        // Con un valore null i setter dell'SDK scrivono comunque il campo (a zero): si imposta solo cio' che c'e'.
        if (avgHr is { } hr)
        {
            m.SetAvgHeartRate(hr);
        }

        if (maxHr is { } max)
        {
            m.SetMaxHeartRate(max);
        }

        if (avgPower is { } power)
        {
            m.SetAvgPower(power);
        }

        if (avgCadence is { } cadence)
        {
            m.SetAvgCadence(cadence);
        }

        if (ascent is { } up)
        {
            m.SetTotalAscent(up);
        }

        return m;
    }

    /// <summary>
    /// Uscita in bici: 61 record (60 s), distanza 510 m, dislivello 12 m, FC media 130 (max 160),
    /// potenza media 210 (150 + 2 i, media su i = 0..60), cadenza 90, due giri da 30 s e 255 m.
    /// </summary>
    public static byte[] Ride(bool includeSession = true, bool enhanced = false)
    {
        var msgs = new List<Mesg> { FileId() };
        for (var i = 0; i <= 60; i++)
        {
            msgs.Add(RideRecord(i, enhanced: enhanced));
            if (i == 30)
            {
                msgs.Add(Lap(Start, 30, 255));
            }
        }

        msgs.Add(Lap(Start.AddSeconds(30), 30, 255));
        if (includeSession)
        {
            msgs.Add(Session(FitSport.Cycling, Start, 60, 60, 510, avgHr: 130, maxHr: 160, avgPower: 210, avgCadence: 90, ascent: 12));
        }

        return Build(msgs);
    }

    /// <summary>
    /// Nuoto in piscina senza GPS ne' quota: 61 record, velocita' 1 m/s (distanza cumulata i metri),
    /// FC costante 120, nessuna potenza ne' cadenza; sessione di 60 s e 60 m.
    /// </summary>
    public static byte[] Swim()
    {
        var msgs = new List<Mesg> { FileId() };
        for (var i = 0; i <= 60; i++)
        {
            var r = new RecordMesg();
            r.SetTimestamp(new FitDateTime(Start.AddSeconds(i)));
            r.SetHeartRate(120);
            r.SetSpeed(1.0f);
            r.SetDistance(1.0f * i);
            msgs.Add(r);
        }

        msgs.Add(Lap(Start, 60, 60));
        var session = Session(FitSport.Swimming, Start, 60, 60, 60, avgHr: 120, maxHr: 120);
        session.SetSubSport(SubSport.LapSwimming);
        msgs.Add(session);
        return Build(msgs);
    }

    /// <summary>
    /// Due sessioni: bici (31 record da Start, 30 s, timer 28 s, 300 m, +5 m) e corsa (31 record da Start + 40 s,
    /// 30 s, timer 30 s, 100 m, +3 m). FC dei record: 100 (bici) e 140 (corsa).
    /// </summary>
    public static byte[] MultiSession()
    {
        var run0 = Start.AddSeconds(40);
        var msgs = new List<Mesg> { FileId() };
        for (var i = 0; i <= 30; i++)
        {
            var r = RideRecord(i, power: false, cadence: false);
            r.SetHeartRate(100);
            msgs.Add(r);
        }

        msgs.Add(Lap(Start, 30, 300));
        msgs.Add(Session(FitSport.Cycling, Start, 30, 28, 300, ascent: 5));
        for (var i = 0; i <= 30; i++)
        {
            var r = RideRecord(i, start: run0, power: false, cadence: false);
            r.SetHeartRate(140);
            msgs.Add(r);
        }

        msgs.Add(Lap(run0, 30, 100));
        msgs.Add(Session(FitSport.Running, run0, 30, 30, 100, ascent: 3));
        return Build(msgs);
    }

    /// <summary>
    /// <paramref name="count"/> record a 1 Hz con tutti i campi (posizione, quota, FC, potenza, cadenza, velocita') e
    /// nessuna sessione: serve ai test sul limite di punti e sulla cancellazione.
    /// </summary>
    public static byte[] Points(int count, bool enhanced = false)
    {
        var msgs = new List<Mesg>(count + 1) { FileId() };
        for (var i = 0; i < count; i++)
        {
            var r = RideRecord(0, enhanced: enhanced);
            r.SetTimestamp(new FitDateTime(Start.AddSeconds(i)));
            r.SetPositionLat(Lat0Semicircles + (i % 100_000) * 10);
            r.SetHeartRate((byte)(100 + i % 80));
            r.SetPower((ushort)(150 + i % 100));
            msgs.Add(r);
        }

        return Build(msgs);
    }
}
