using Dynastream.Fit;
using ActivitySport = Metrica.Contracts.Sport;
using LapData = Metrica.Contracts.LapData;
using StreamLimits = Metrica.Contracts.StreamLimits;

namespace Metrica.Ingestion.Parsing;

/// <summary>
/// Parser FIT con l'SDK ufficiale Garmin (<c>Garmin.FIT.Sdk</c>); la libreria resta confinata in questa classe.
/// </summary>
/// <remarks>
/// <para><b>Integrita'.</b> Si verificano intestazione FIT e CRC dell'intero file prima di decodificare: file troncati,
/// corrotti o non FIT sono rifiutati. Il contenuto e' copiato in memoria (al massimo 32 MiB, controllando il token)
/// perche' l'SDK richiede un flusso scorrevole e l'upload potrebbe non esserlo.</para>
/// <para><b>Record.</b> Si leggono <c>Record</c> con timestamp (quelli senza timestamp valido si scartano): posizione in
/// semicerchi convertita in gradi (<c>gradi = semicerchi * 180 / 2^31</c>), <c>enhanced_altitude</c> o <c>altitude</c>,
/// <c>heart_rate</c>, <c>power</c>, <c>cadence</c>, <c>enhanced_speed</c> o <c>speed</c>, distanza cumulata. Un file
/// senza GPS ha le colonne di posizione <c>null</c>. Un file senza record e' rifiutato.</para>
/// <para><b>Sessione.</b> Con una sola <c>Session</c> lo sport viene da <c>Session.Sport</c> (Running, Cycling, Swimming;
/// altro: Other) e i valori di riepilogo (inizio, tempo trascorso, tempo in movimento = tempo del timer, distanza,
/// ascesa, FC/potenza/cadenza media e FC massima) sostituiscono quelli calcolati dai record, che restano il ripiego
/// per ogni valore assente. Senza sessione lo sport viene dal messaggio <c>Sport</c> o, in mancanza, e' Other.
/// Con <b>piu' sessioni</b> (multisport) l'attivita' e' una sola, sport Other, tempo trascorso, tempo in movimento,
/// distanza e ascesa sono sommati tra le sessioni (se una sessione non ha il valore, si calcola dai record), i record
/// sono concatenati in ordine di tempo e le medie e il massimo sono calcolati dai record.</para>
/// <para><b>Giri.</b> Un giro per messaggio <c>Lap</c> con istante di inizio valido, in ordine di inizio.</para>
/// <para><b>Cancellazione.</b> Il token e' controllato a ogni messaggio decodificato e a ogni blocco di byte copiato.</para>
/// </remarks>
public sealed class FitParser : IActivityFileParser
{
    private const long MaxInputBytes = 32L * 1024 * 1024;

    /// <summary>Sotto questo valore un timestamp FIT non e' una data reale (e' un tempo relativo del dispositivo).</summary>
    private const uint MinimumRealTimestamp = 0x10000000;

    private const string Invalid = "Il file non e' un FIT valido.";
    private const string Corrupt = "Il file FIT e' incompleto o danneggiato.";
    private const string TooLarge = "Il file FIT e' troppo grande.";
    private const string TooManyLaps = "Il file FIT contiene troppi giri.";
    private const string TooManySessions = "Il file FIT contiene troppe sessioni.";

    public bool CanParse(string fileName)
        => !string.IsNullOrEmpty(fileName)
           && string.Equals(Path.GetExtension(fileName), ".fit", StringComparison.OrdinalIgnoreCase);

    public ParsedActivity Parse(Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Il flusso non e' leggibile.", nameof(content));
        }

        ct.ThrowIfCancellationRequested();
        var buffer = CopyToMemory(content, ct);
        try
        {
            return ParseFit(buffer, ct);
        }
        catch (ActivityFileException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // L'SDK lancia FitException e, su dati incoerenti, altre eccezioni: nessuna esce, e nessun messaggio
            // dell'SDK (puo' citare dati del file) finisce in quello restituito.
            throw new ActivityFileException(Corrupt);
        }
    }

    private static MemoryStream CopyToMemory(Stream source, CancellationToken ct)
    {
        var memory = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (memory.Length + read > MaxInputBytes)
            {
                throw new ActivityFileException(TooLarge);
            }

            memory.Write(chunk, 0, read);
        }

        memory.Position = 0;
        return memory;
    }

    private static ParsedActivity ParseFit(MemoryStream buffer, CancellationToken ct)
    {
        var decode = new Decode();
        if (!decode.IsFIT(buffer))
        {
            throw new ActivityFileException(Invalid);
        }

        buffer.Position = 0;
        if (!decode.CheckIntegrity(buffer))
        {
            throw new ActivityFileException(Corrupt);
        }

        ct.ThrowIfCancellationRequested();
        buffer.Position = 0;
        var collector = new Collector(ct);
        decode.MesgEvent += collector.OnMesg;
        if (!decode.Read(buffer))
        {
            throw new ActivityFileException(Corrupt);
        }

        return collector.Build();
    }

    /// <summary>I soli valori di un messaggio Session che servono alla sintesi (gia' validati).</summary>
    private readonly record struct SessionData(
        Sport? Sport,
        DateTimeOffset? Start,
        double? Elapsed,
        double? Timer,
        double? Distance,
        double? Ascent,
        double? AvgHeartRate,
        double? MaxHeartRate,
        double? AvgPower,
        double? AvgCadence);

    /// <summary>Raccoglie i messaggi durante la decodifica.</summary>
    private sealed class Collector
    {
        private readonly CancellationToken _ct;
        private readonly List<RawPoint> _points = [];
        private readonly List<LapData> _laps = [];
        private readonly List<SessionData> _sessions = [];
        private Sport? _declaredSport;
        private int _lapMessages;

        public Collector(CancellationToken ct) => _ct = ct;

        public void OnMesg(object? sender, MesgEventArgs e)
        {
            _ct.ThrowIfCancellationRequested();
            switch (e.mesg.Num)
            {
                case MesgNum.Record:
                    AddRecord(new RecordMesg(e.mesg));
                    break;
                case MesgNum.Lap:
                    AddLap(new LapMesg(e.mesg));
                    break;
                case MesgNum.Session:
                    AddSession(new SessionMesg(e.mesg));
                    break;
                case MesgNum.Sport:
                    _declaredSport ??= new SportMesg(e.mesg).GetSport();
                    break;
            }
        }

        private void AddRecord(RecordMesg record)
        {
            if (ToUtc(record.GetTimestamp()) is not { } time)
            {
                return;
            }

            if (_points.Count >= StreamLimits.MaxPoints)
            {
                throw new TooManyPointsException();
            }

            Mesg mesg = record;
            double? lat = null, lon = null;
            if (Number(mesg, RecordMesg.FieldDefNum.PositionLat, record.GetPositionLat()) is { } semicirclesLat
                && Number(mesg, RecordMesg.FieldDefNum.PositionLong, record.GetPositionLong()) is { } semicirclesLon)
            {
                var latitude = semicirclesLat * DegreesPerSemicircle;
                if (latitude is >= -90 and <= 90)
                {
                    lat = latitude;
                    lon = semicirclesLon * DegreesPerSemicircle;
                }
            }

            _points.Add(new RawPoint(
                time,
                lat,
                lon,
                Number(mesg, RecordMesg.FieldDefNum.EnhancedAltitude, record.GetEnhancedAltitude())
                    ?? Number(mesg, RecordMesg.FieldDefNum.Altitude, record.GetAltitude()),
                Number(mesg, RecordMesg.FieldDefNum.HeartRate, record.GetHeartRate()),
                Number(mesg, RecordMesg.FieldDefNum.Power, record.GetPower()),
                Number(mesg, RecordMesg.FieldDefNum.Cadence, record.GetCadence()),
                Number(mesg, RecordMesg.FieldDefNum.EnhancedSpeed, record.GetEnhancedSpeed())
                    ?? Number(mesg, RecordMesg.FieldDefNum.Speed, record.GetSpeed()),
                Number(mesg, RecordMesg.FieldDefNum.Distance, record.GetDistance())));
        }

        private void AddLap(LapMesg lap)
        {
            if (++_lapMessages > Plausibility.MaxLaps)
            {
                throw new ActivityFileException(TooManyLaps);
            }

            if (ToUtc(lap.GetStartTime()) is { } start)
            {
                _laps.Add(ActivityAssembler.MakeLap(
                    start,
                    NonNegative(lap, LapMesg.FieldDefNum.TotalElapsedTime, lap.GetTotalElapsedTime()) ?? 0,
                    NonNegative(lap, LapMesg.FieldDefNum.TotalDistance, lap.GetTotalDistance()) ?? 0));
            }
        }

        /// <summary>Conserva dei messaggi Session solo i valori necessari (non l'oggetto dell'SDK).</summary>
        private void AddSession(SessionMesg s)
        {
            if (_sessions.Count >= Plausibility.MaxSessions)
            {
                throw new ActivityFileException(TooManySessions);
            }

            _sessions.Add(new SessionData(
                s.GetSport(),
                ToUtc(s.GetStartTime()),
                NonNegative(s, SessionMesg.FieldDefNum.TotalElapsedTime, s.GetTotalElapsedTime()),
                NonNegative(s, SessionMesg.FieldDefNum.TotalTimerTime, s.GetTotalTimerTime()),
                NonNegative(s, SessionMesg.FieldDefNum.TotalDistance, s.GetTotalDistance()),
                Number(s, SessionMesg.FieldDefNum.TotalAscent, s.GetTotalAscent()),
                Number(s, SessionMesg.FieldDefNum.AvgHeartRate, s.GetAvgHeartRate()),
                Number(s, SessionMesg.FieldDefNum.MaxHeartRate, s.GetMaxHeartRate()),
                Number(s, SessionMesg.FieldDefNum.AvgPower, s.GetAvgPower()),
                Number(s, SessionMesg.FieldDefNum.AvgCadence, s.GetAvgCadence())));
        }

        public ParsedActivity Build()
        {
            var sport = _sessions.Count switch
            {
                0 => MapSport(_declaredSport),
                1 => MapSport(_sessions[0].Sport),
                _ => ActivitySport.Other,
            };

            return ActivityAssembler.Build(sport, _points, _laps, BuildOverrides());
        }

        private ActivityAssembler.Overrides? BuildOverrides()
        {
            if (_sessions.Count == 0)
            {
                return null;
            }

            var start = _sessions.Select(s => s.Start).Where(t => t.HasValue).Min();

            if (_sessions.Count == 1)
            {
                var s = _sessions[0];
                return new ActivityAssembler.Overrides(
                    start, s.Elapsed, s.Timer, s.Distance, s.Ascent, s.AvgHeartRate, s.MaxHeartRate, s.AvgPower, s.AvgCadence);
            }

            return new ActivityAssembler.Overrides(
                start,
                SumIfAll(s => s.Elapsed),
                SumIfAll(s => s.Timer),
                SumIfAll(s => s.Distance),
                SumIfAll(s => s.Ascent));
        }

        /// <summary>Somma il valore delle sessioni; <c>null</c> se anche una sola non lo ha.</summary>
        private double? SumIfAll(Func<SessionData, double?> value)
        {
            double sum = 0;
            foreach (var session in _sessions)
            {
                if (value(session) is not { } v)
                {
                    return null;
                }

                sum += v;
            }

            return sum;
        }
    }

    private static DateTimeOffset? ToUtc(Dynastream.Fit.DateTime? timestamp)
    {
        if (timestamp is null || timestamp.GetTimeStamp() < MinimumRealTimestamp)
        {
            return null;
        }

        var utc = System.DateTime.SpecifyKind(timestamp.GetDateTime(), DateTimeKind.Utc);
        return new DateTimeOffset(utc);
    }

    private const double DegreesPerSemicircle = 180.0 / 2_147_483_648.0;

    /// <summary>
    /// Valore numerico di un campo, o <c>null</c> se il campo manca o contiene il valore "non valido" del suo tipo
    /// base (0xFF, 0xFFFF, ...). L'SDK restituisce quel valore come dato (e scalato, per quota e velocita'), mentre
    /// dispositivi e codificatori lo scrivono per un campo assente: va letto sul valore grezzo.
    /// </summary>
    private static double? Number<T>(Mesg mesg, byte fieldNumber, T? value)
        where T : struct, System.Numerics.INumber<T>
    {
        if (value is not { } v || !HasValidRawValue(mesg, fieldNumber))
        {
            return null;
        }

        var number = double.CreateChecked(v);
        return double.IsFinite(number) ? number : null;
    }

    private static double? NonNegative<T>(Mesg mesg, byte fieldNumber, T? value)
        where T : struct, System.Numerics.INumber<T>
        => Number(mesg, fieldNumber, value) is { } v and >= 0 ? v : null;

    private static bool HasValidRawValue(Mesg mesg, byte fieldNumber)
    {
        var field = mesg.GetField(fieldNumber);
        if (field is null || field.GetNumValues() == 0 || field.GetRawValue(0) is not { } raw)
        {
            return false;
        }

        return !raw.Equals(Fit.BaseType[field.Type & Fit.BaseTypeNumMask].invalidValue);
    }

    private static ActivitySport MapSport(Sport? sport) => sport switch
    {
        Sport.Running => ActivitySport.Run,
        Sport.Cycling => ActivitySport.Ride,
        Sport.Swimming => ActivitySport.Swim,
        _ => ActivitySport.Other,
    };
}
