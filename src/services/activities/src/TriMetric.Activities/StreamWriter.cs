using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Scrive i punti di uno <see cref="StreamColumns"/> in <c>activities.activity_stream</c> con COPY binario di Npgsql
/// (REQ-016, DEC-003): decine di migliaia di punti in una frazione di secondo, dove un <c>SaveChanges</c> sarebbe lento.
/// </summary>
/// <remarks>
/// <para><b>Transazione.</b> Usa la connessione e la transazione CORRENTI del <see cref="ActivitiesDbContext"/>: il chiamante
/// apre la transazione (<c>Database.BeginTransaction</c>) e, nello stesso scope, scrive inbox, attivita' e punti, cosi' tutto
/// si conferma o si annulla insieme (T12). Il writer non apre mai una connessione o una transazione propria: senza una
/// transazione attiva sul contesto lancia <see cref="InvalidOperationException"/> (altrimenti il COPY sarebbe confermato
/// subito, fuori dall'atomicita' con inbox e gestore). Non e' supportata una transazione che il contesto non conosce (ad
/// esempio una <c>TransactionScope</c> ambientale o una transazione aperta sulla connessione senza passare da EF).</para>
/// <para><b>Validazione.</b> Prima di avviare il COPY, in una passata senza allocazioni, verifica che tutte le colonne
/// presenti siano lunghe quanto <see cref="StreamColumns.TimeOffsetSeconds"/> e che gli scarti siano finiti e nell'intervallo
/// rappresentabile: <see cref="ArgumentException"/> con SOLO nomi e conteggi (nessun valore: REQ-034) e nessuna riga scritta,
/// transazione intatta. Il writer non scrive mai righe sbilanciate. La validazione dell'evento verso la DLQ e' di T12.
/// Un errore DURANTE il COPY (rete, annullamento) lascia invece la transazione da annullare, come per qualunque errore
/// del server: il chiamante fa il rollback.</para>
/// <para><b>Tempo.</b> <c>time = startTime + offset</c>, in UTC. Lo scarto e' convertito in microsecondi interi
/// (<c>Math.Round(offset * 1e6)</c>, la risoluzione di <c>timestamptz</c>) e sommato in tick all'istante di inizio: nessuna
/// somma cumulativa in virgola mobile, quindi nessuna deriva. Gli scarti gia' arrotondati al millisecondo da ingestion
/// (DEC-012), che in binario non sono esatti (0,001, 12,345...), tornano esattamente al millisecondo. Gli scarti negativi non
/// sono rifiutati (il contratto li vieta al produttore); l'inizio puo' avere qualunque offset: conta l'istante UTC.</para>
/// <para><b>Valori.</b> Una colonna <c>null</c> (intera assente) e un valore <c>null</c> dentro la colonna sono scritti come NULL.
/// Il COPY nomina le colonne in modo esplicito (<see cref="Columns"/>), verificate da un test contro modello EF e schema reale.
/// Si legge in streaming dagli elenchi dello stream: nessuna copia dei punti (100.000 punti = una riga alla volta).</para>
/// <para>Nessun log (REQ-034). Ha stato solo nel contesto che riceve: si registra con ambito (scoped) e non e' thread-safe.</para>
/// </remarks>
public sealed class StreamWriter(ActivitiesDbContext context)
{
    /// <summary>Colonna del COPY: nome nella tabella e tipo Npgsql con cui si scrive.</summary>
    public readonly record struct CopyColumn(string Name, NpgsqlDbType DbType);

    /// <summary>Tabella di destinazione, con lo schema (il <c>search_path</c> del ruolo non e' dato per scontato).</summary>
    public const string TableName = "activities.activity_stream";

    // Colonne di valori, nell'ordine in cui si scrivono dopo activity_id e time. Sorgente unica: Columns e il COPY derivano da qui.
    private static readonly (string Column, string Property, Func<StreamColumns, IReadOnlyList<double?>?> Select)[] ValueColumns =
    [
        ("latitude", nameof(StreamColumns.Latitude), s => s.Latitude),
        ("longitude", nameof(StreamColumns.Longitude), s => s.Longitude),
        ("altitude_meters", nameof(StreamColumns.AltitudeMeters), s => s.AltitudeMeters),
        ("heart_rate", nameof(StreamColumns.HeartRate), s => s.HeartRate),
        ("power_watts", nameof(StreamColumns.PowerWatts), s => s.PowerWatts),
        ("cadence", nameof(StreamColumns.Cadence), s => s.Cadence),
        ("speed_mps", nameof(StreamColumns.SpeedMetersPerSecond), s => s.SpeedMetersPerSecond),
    ];

    /// <summary>Colonne del COPY, nell'ordine in cui sono scritte (e dichiarate nel comando).</summary>
    public static IReadOnlyList<CopyColumn> Columns { get; } =
    [
        new("activity_id", NpgsqlDbType.Uuid),
        new("time", NpgsqlDbType.TimestampTz),
        .. ValueColumns.Select(c => new CopyColumn(c.Column, NpgsqlDbType.Double)),
    ];

    private static readonly string CopyCommand =
        $"COPY {TableName} ({string.Join(", ", Columns.Select(c => c.Name))}) FROM STDIN (FORMAT BINARY)";

    // Tetto sul valore assoluto di uno scarto: ~10.000 anni, oltre l'intervallo di DateTime; evita l'overflow dei tick.
    private const double MaxOffsetSeconds = 3.2e11;

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// Scrive tutti i punti dello stream per l'attivita' indicata e restituisce il numero di righe scritte.
    /// </summary>
    /// <param name="activityId">Attivita' a cui appartengono i punti (scritta su ogni riga).</param>
    /// <param name="startTime">Inizio dell'attivita': <c>time</c> = inizio + scarto.</param>
    /// <param name="stream">Colonne parallele dello stream (tutte lunghe quanto <c>TimeOffsetSeconds</c>, o <c>null</c>).</param>
    /// <exception cref="ArgumentException">Colonne di lunghezza diversa, scarti non finiti o fuori intervallo.</exception>
    /// <exception cref="InvalidOperationException">Nessuna transazione attiva sul contesto, o connessione non aperta.</exception>
    public async Task<long> WriteAsync(Guid activityId, DateTimeOffset startTime, StreamColumns stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.TimeOffsetSeconds is null)
        {
            throw new ArgumentException("TimeOffsetSeconds e' obbligatorio.", nameof(stream));
        }

        var offsets = stream.TimeOffsetSeconds;
        var count = offsets.Count;
        var columns = new IReadOnlyList<double?>?[ValueColumns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            columns[c] = ValueColumns[c].Select(stream);
            if (columns[c] is { } column && column.Count != count)
            {
                throw new ArgumentException(
                    $"La colonna {ValueColumns[c].Property} ha {column.Count} valori ma TimeOffsetSeconds ne ha {count}: "
                    + "le colonne dello stream devono avere tutte la stessa lunghezza.",
                    nameof(stream));
            }
        }

        var startTicks = startTime.UtcTicks;
        for (var i = 0; i < count; i++)
        {
            _ = TimeTicks(startTicks, offsets[i], i);   // lancia ArgumentException prima di toccare il database
        }

        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "StreamWriter richiede una transazione attiva sul contesto (Database.BeginTransaction): il COPY deve "
                + "confermarsi o annullarsi insieme alle altre scritture, non da solo. Non apre transazioni proprie.");
        }

        if (context.Database.GetDbConnection() is not NpgsqlConnection connection)
        {
            throw new InvalidOperationException("StreamWriter richiede una connessione Npgsql (COPY binario).");
        }

        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "La connessione del contesto non e' aperta (stato " + connection.State + "): la transazione e' stata persa.");
        }

        // La transazione deve essere davvero quella di questa connessione: se la DbConnection e' stata chiusa e riaperta
        // direttamente (non con l'API di EF), CurrentTransaction resta valorizzata ma la transazione Npgsql e' ormai senza
        // connessione e il COPY andrebbe in autocommit, fuori dal rollback del chiamante.
        if (context.Database.CurrentTransaction.GetDbTransaction() is not NpgsqlTransaction { Connection: { } transactionConnection }
            || !ReferenceEquals(transactionConnection, connection))
        {
            throw new InvalidOperationException(
                "La transazione del contesto non e' piu' associata alla connessione corrente (connessione chiusa e riaperta?): "
                + "il COPY non verrebbe annullato da un rollback.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var importer = await connection.BeginBinaryImportAsync(CopyCommand, cancellationToken);
        for (var i = 0; i < count; i++)
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(activityId, Columns[0].DbType, cancellationToken);
            await importer.WriteAsync(new DateTime(TimeTicks(startTicks, offsets[i], i), DateTimeKind.Utc), Columns[1].DbType, cancellationToken);
            for (var c = 0; c < columns.Length; c++)
            {
                if (columns[c]?[i] is { } value)
                {
                    await importer.WriteAsync(value, Columns[c + 2].DbType, cancellationToken);
                }
                else
                {
                    await importer.WriteNullAsync(cancellationToken);
                }
            }
        }

        return (long)await importer.CompleteAsync(cancellationToken);
    }

    /// <summary>Tick UTC del punto: inizio piu' scarto arrotondato al microsecondo, in aritmetica intera.</summary>
    private static long TimeTicks(long startTicks, double offsetSeconds, int index)
    {
        if (!double.IsFinite(offsetSeconds) || Math.Abs(offsetSeconds) > MaxOffsetSeconds)
        {
            throw new ArgumentException($"Lo scarto di tempo del punto {index} non e' finito o e' fuori intervallo.", "stream");
        }

        var microseconds = (long)Math.Round(offsetSeconds * 1_000_000d, MidpointRounding.AwayFromZero);
        var ticks = startTicks + microseconds * TicksPerMicrosecond;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentException($"L'istante del punto {index} e' fuori dall'intervallo rappresentabile.", "stream");
        }

        return ticks;
    }
}
