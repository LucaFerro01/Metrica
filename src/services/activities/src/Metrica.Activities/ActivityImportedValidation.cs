using Metrica.Contracts;
using Metrica.Messaging;

namespace Metrica.Activities;

/// <summary>
/// Validazione esplicita di <c>ActivityImported</c> PRIMA di toccare il database (REQ-014, REQ-017, REQ-034; DEC-005).
/// Un evento non valido lancia <see cref="EventValidationException"/>: il consumatore lo manda in DLQ al primo tentativo,
/// senza ritentativi. Si intercetta TUTTO cio' che il database o <see cref="StreamWriter"/> rifiuterebbero in modo
/// deterministico: sarebbe altrimenti un errore generico ritentato fino a <c>MaxDeliver</c> prima della DLQ.
/// </summary>
/// <remarks>
/// <para>Rifiuta: busta nulla, <c>Id</c> o <c>UserId</c> vuoti, <c>Version</c> diversa da <see cref="SupportedVersion"/>, <c>Type</c> diverso
/// da <see cref="EventNames.ActivityImported"/>, <c>Data</c> nulla; <c>Source</c> e <c>ExternalId</c> nulli, vuoti, solo
/// spazi, oltre <see cref="MaxKeyLength"/> caratteri o con caratteri che PostgreSQL non memorizza (NUL, surrogati isolati);
/// <c>Sport</c> non definito; istanti che Npgsql scrive come infinito (compreso il valore predefinito di un campo mancante);
/// numeri non finiti in durata, distanza, dislivello, riepilogo, giri e valori dello stream; <c>Summary</c>, <c>Laps</c> (o un
/// suo elemento), <c>Stream</c> o <c>TimeOffsetSeconds</c> nulli; piu' di <see cref="StreamLimits.MaxPoints"/> punti; colonne
/// di lunghezza diversa da <c>TimeOffsetSeconds</c> e scarti fuori intervallo (tramite <see cref="StreamWriter.Validate"/>).</para>
/// <para><b>Limiti noti.</b> La deserializzazione JSON non distingue un campo mancante da uno con il valore predefinito per i
/// tipi valore: uno <c>sport</c> mancante diventa <c>Run</c> (il primo valore dell'enum) e NON e' rilevabile qui, mentre uno
/// <c>startTime</c> mancante e' rilevabile perche' il predefinito (anno 1) e' un istante non ammesso. Numeri mancanti diventano
/// 0 (chiuderlo richiede di rendere obbligatori i campi nei contratti, fuori ambito di T12: seguito registrato). Uno <c>sport</c>
/// sconosciuto come testo o un <c>data</c> nullo non arrivano nemmeno qui: il consumatore li manda in DLQ come <c>Malformed</c>.
/// Nessuna regola semantica oltre alle precedenti (distanze o durate negative, istanti lontani, stream con 0 punti): le
/// garantisce il produttore (ingestion).</para>
/// <para>I messaggi delle eccezioni riportano solo nomi di campo, indici e conteggi, mai valori (REQ-034).</para>
/// </remarks>
public static class ActivityImportedValidation
{
    /// <summary>Lunghezza massima di <c>Source</c> e <c>ExternalId</c> (l'indice univoco ha un limite di riga: un valore enorme farebbe fallire l'INSERT ad ogni tentativo).</summary>
    public const int MaxKeyLength = 256;

    /// <summary>Versione del contratto di <c>ActivityImported</c> che questo gestore sa interpretare.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Valida la busta; lancia <see cref="EventValidationException"/> alla prima violazione.</summary>
    public static void Validate(EventEnvelope<ActivityImported>? envelope)
    {
        if (envelope is null)
        {
            throw Invalid("La busta dell'evento e' nulla.");
        }

        if (envelope.Id == Guid.Empty)
        {
            throw Invalid("L'Id della busta e' vuoto.");
        }

        if (envelope.UserId == Guid.Empty)
        {
            throw Invalid("Lo UserId della busta e' vuoto.");
        }

        // DEC-005: un cambio incompatibile del contratto incrementa la versione. Un evento di versione o tipo diversi non va
        // interpretato con la semantica v1 (salvataggio silenzioso di dati sbagliati): resta in DLQ, conservato per intero.
        if (envelope.Version != SupportedVersion)
        {
            throw Invalid($"La versione della busta non e' supportata (attesa {SupportedVersion}).");
        }

        if (!string.Equals(envelope.Type, EventNames.ActivityImported, StringComparison.Ordinal))
        {
            throw Invalid($"Il tipo della busta non e' {EventNames.ActivityImported}.");
        }

        if (envelope.Data is not { } data)
        {
            throw Invalid("Data e' nulla.");
        }

        RequireKey(data.Source, nameof(ActivityImported.Source));
        RequireKey(data.ExternalId, nameof(ActivityImported.ExternalId));

        if (!Enum.IsDefined(data.Sport))
        {
            throw Invalid("Sport non e' un valore definito (run, ride, swim, other).");
        }

        RequireInstant(data.StartTime, nameof(ActivityImported.StartTime));
        RequireFinite(data.ElapsedSeconds, nameof(ActivityImported.ElapsedSeconds));
        RequireFinite(data.MovingSeconds, nameof(ActivityImported.MovingSeconds));
        RequireFinite(data.DistanceMeters, nameof(ActivityImported.DistanceMeters));
        RequireFinite(data.ElevationGainMeters, nameof(ActivityImported.ElevationGainMeters));

        if (data.Summary is not { } summary)
        {
            throw Invalid("Summary e' nullo.");
        }

        RequireFinite(summary.AvgHeartRate, nameof(ActivitySummary.AvgHeartRate));
        RequireFinite(summary.MaxHeartRate, nameof(ActivitySummary.MaxHeartRate));
        RequireFinite(summary.AvgPowerWatts, nameof(ActivitySummary.AvgPowerWatts));
        RequireFinite(summary.AvgCadence, nameof(ActivitySummary.AvgCadence));

        if (data.Laps is not { } laps)
        {
            throw Invalid("Laps e' nullo.");
        }

        for (var i = 0; i < laps.Count; i++)
        {
            if (laps[i] is not { } lap)
            {
                throw Invalid($"Il giro {i} e' nullo.");
            }

            RequireInstant(lap.StartTime, $"Laps[{i}].{nameof(LapData.StartTime)}");
            RequireFinite(lap.ElapsedSeconds, $"Laps[{i}].{nameof(LapData.ElapsedSeconds)}");
            RequireFinite(lap.DistanceMeters, $"Laps[{i}].{nameof(LapData.DistanceMeters)}");
        }

        ValidateStream(data);
    }

    private static void ValidateStream(ActivityImported data)
    {
        if (data.Stream is not { } stream)
        {
            throw Invalid("Stream e' nullo.");
        }

        if (stream.TimeOffsetSeconds is not { } offsets)
        {
            throw Invalid("TimeOffsetSeconds e' nullo.");
        }

        if (offsets.Count > StreamLimits.MaxPoints)
        {
            throw Invalid($"Lo stream ha {offsets.Count} punti: il massimo e' {StreamLimits.MaxPoints}.");
        }

        // Lunghezza delle colonne, scarti finiti e nell'intervallo, istanti rappresentabili: gli stessi controlli del writer.
        try
        {
            StreamWriter.Validate(data.StartTime, stream);
        }
        catch (ArgumentException ex)
        {
            throw new EventValidationException($"Stream non valido: {ex.Message}", ex);
        }

        RequireFiniteValues(stream.Latitude, nameof(StreamColumns.Latitude));
        RequireFiniteValues(stream.Longitude, nameof(StreamColumns.Longitude));
        RequireFiniteValues(stream.AltitudeMeters, nameof(StreamColumns.AltitudeMeters));
        RequireFiniteValues(stream.HeartRate, nameof(StreamColumns.HeartRate));
        RequireFiniteValues(stream.PowerWatts, nameof(StreamColumns.PowerWatts));
        RequireFiniteValues(stream.Cadence, nameof(StreamColumns.Cadence));
        RequireFiniteValues(stream.SpeedMetersPerSecond, nameof(StreamColumns.SpeedMetersPerSecond));
    }

    private static void RequireKey(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{field} e' nullo, vuoto o fatto di soli spazi.");
        }

        if (value.Length > MaxKeyLength)
        {
            throw Invalid($"{field} supera {MaxKeyLength} caratteri.");
        }

        if (!IsStorableText(value))
        {
            throw Invalid($"{field} contiene caratteri che il database non memorizza (NUL o surrogati isolati).");
        }
    }

    /// <summary>PostgreSQL rifiuta NUL nel testo e Npgsql rifiuta i surrogati UTF-16 isolati: ritentare non servirebbe.</summary>
    private static bool IsStorableText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0' || char.IsLowSurrogate(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
        }

        return true;
    }

    /// <summary>Npgsql scrive gli istanti minimo e massimo di <c>DateTime</c> come -infinity/infinity: non sono istanti reali (e il predefinito di un campo mancante e' il minimo).</summary>
    private static void RequireInstant(DateTimeOffset value, string field)
    {
        if (value.UtcTicks <= DateTime.MinValue.Ticks || value.UtcTicks >= DateTime.MaxValue.Ticks)
        {
            throw Invalid($"{field} non e' un istante valido (mancante o agli estremi del calendario).");
        }
    }

    private static void RequireFinite(double value, string field)
    {
        if (!double.IsFinite(value))
        {
            throw Invalid($"{field} non e' un numero finito.");
        }
    }

    private static void RequireFinite(double? value, string field)
    {
        if (value is { } number)
        {
            RequireFinite(number, field);
        }
    }

    private static void RequireFiniteValues(IReadOnlyList<double?>? column, string field)
    {
        if (column is null)
        {
            return;
        }

        for (var i = 0; i < column.Count; i++)
        {
            if (column[i] is { } value && !double.IsFinite(value))
            {
                throw Invalid($"La colonna {field} contiene un valore non finito al punto {i}.");
            }
        }
    }

    private static EventValidationException Invalid(string message) => new(message);
}
