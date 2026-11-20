using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Metrica.Contracts;
using Metrica.Messaging;

namespace Metrica.Activities;

/// <summary>
/// Salva un <c>ActivityImported</c> (REQ-014, REQ-015, REQ-016; AC-006, AC-014; DEC-001, DEC-005): attivita', giri e punti dello
/// stream, in modo idempotente e atomico.
/// </summary>
/// <remarks>
/// <para><b>Transazione.</b> Il gestore gira dentro la transazione che il consumatore apre sul contesto (la stessa dell'inbox):
/// non apre transazioni proprie e non fa commit. Senza una transazione attiva lancia <see cref="InvalidOperationException"/>
/// prima di scrivere (altrimenti l'<c>INSERT</c> dell'attivita' sarebbe confermata da sola e un guasto successivo lascerebbe
/// un'attivita' senza giri e punti). Qualunque errore dopo la validazione annulla tutto (attivita', giri, punti, riga di inbox)
/// e fa ritentare il messaggio.</para>
/// <para><b>Ordine.</b> 1) validazione completa (<see cref="ActivityImportedValidation"/>) PRIMA di toccare il database: un
/// evento non valido finisce in DLQ senza ritentativi; 2) <c>INSERT ... ON CONFLICT (user_id, source, external_id) DO NOTHING</c>
/// con SQL parametrizzato (righe inserite 1 = nuova, 0 = gia' presente: nessun errore, nessun ritentativo); 3) SOLO se l'attivita' e'
/// nuova, i giri (<c>SaveChanges</c>) e i punti (<see cref="StreamWriter"/>, COPY binario nella stessa connessione). Due eventi con
/// <c>Id</c> diversi e la stessa chiave producono una sola attivita': il secondo non aggiunge ne' giri ne' punti. Lo stesso evento
/// (stesso <c>Id</c>) non arriva nemmeno qui: lo ferma l'inbox del consumatore, nel database.</para>
/// <para><b>Dati.</b> <c>user_id</c> e' SEMPRE <c>envelope.UserId</c>; <c>sport</c> e' il testo minuscolo di <see cref="SportConverter"/>;
/// <c>created_at</c> e' l'ora UTC dell'elaborazione; gli istanti sono scritti in UTC (un <c>DateTimeOffset</c> con offset non zero
/// non e' scrivibile cosi' com'e' su <c>timestamptz</c>). Nomi di tabella e colonne vengono dal modello EF (nessun nome scritto a mano).</para>
/// <para>Nessun log (REQ-034): ne' coordinate, ne' frequenza cardiaca, ne' identificativi dell'evento.</para>
/// </remarks>
public sealed class ActivityImportedHandler(ActivitiesDbContext db, StreamWriter streamWriter) : IEventHandler<ActivityImported>
{
    /// <summary>Colonna dell'<c>INSERT</c>: proprieta' del modello (da cui si ricava il nome), tipo Npgsql e valore.</summary>
    private sealed record InsertColumn(string Property, NpgsqlDbType Type, Func<InsertValues, object?> Value);

    private sealed record InsertValues(Guid Id, Guid UserId, ActivityImported Data, DateTime CreatedAt);

    private static readonly InsertColumn[] InsertColumns =
    [
        new(nameof(Activity.Id), NpgsqlDbType.Uuid, v => v.Id),
        new(nameof(Activity.UserId), NpgsqlDbType.Uuid, v => v.UserId),
        new(nameof(Activity.Source), NpgsqlDbType.Text, v => v.Data.Source),
        new(nameof(Activity.ExternalId), NpgsqlDbType.Text, v => v.Data.ExternalId),
        new(nameof(Activity.Sport), NpgsqlDbType.Text, v => SportConverter.ToText(v.Data.Sport)),
        new(nameof(Activity.StartTime), NpgsqlDbType.TimestampTz, v => v.Data.StartTime.UtcDateTime),
        new(nameof(Activity.ElapsedSeconds), NpgsqlDbType.Double, v => v.Data.ElapsedSeconds),
        new(nameof(Activity.MovingSeconds), NpgsqlDbType.Double, v => v.Data.MovingSeconds),
        new(nameof(Activity.DistanceMeters), NpgsqlDbType.Double, v => v.Data.DistanceMeters),
        new(nameof(Activity.ElevationGainMeters), NpgsqlDbType.Double, v => v.Data.ElevationGainMeters),
        new(nameof(Activity.AvgHeartRate), NpgsqlDbType.Double, v => v.Data.Summary.AvgHeartRate),
        new(nameof(Activity.MaxHeartRate), NpgsqlDbType.Double, v => v.Data.Summary.MaxHeartRate),
        new(nameof(Activity.AvgPowerWatts), NpgsqlDbType.Double, v => v.Data.Summary.AvgPowerWatts),
        new(nameof(Activity.AvgCadence), NpgsqlDbType.Double, v => v.Data.Summary.AvgCadence),
        new(nameof(Activity.CreatedAt), NpgsqlDbType.TimestampTz, v => v.CreatedAt),
    ];

    /// <summary>Proprieta' della chiave di deduplicazione (<c>ux_activities_user_source_external</c>).</summary>
    private static readonly string[] ConflictProperties =
        [nameof(Activity.UserId), nameof(Activity.Source), nameof(Activity.ExternalId)];

    private static string? _insertSql;

    /// <inheritdoc />
    public async Task HandleAsync(EventEnvelope<ActivityImported> envelope, CancellationToken ct)
    {
        // 1) Validazione completa PRIMA di qualunque accesso al database (EventValidationException = DLQ, nessun retry).
        ActivityImportedValidation.Validate(envelope);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Il gestore di ActivityImported richiede la transazione del consumatore (nessuna transazione attiva sul contesto): "
                + "senza, l'attivita' sarebbe confermata prima di giri e punti.");
        }

        var data = envelope.Data;

        // 2) INSERT ... ON CONFLICT DO NOTHING: la riga dice se l'attivita' e' nuova. user_id viene solo dalla busta.
        var values = new InsertValues(Guid.NewGuid(), envelope.UserId, data, DateTime.UtcNow);
        var inserted = await db.Database.ExecuteSqlRawAsync(
            InsertSql(db),
            [.. InsertColumns.Select(c => c.Value(values) switch
            {
                null => new NpgsqlParameter(c.Property, c.Type) { Value = DBNull.Value },
                var value => new NpgsqlParameter(c.Property, c.Type) { Value = value },
            })],
            ct);
        if (inserted == 0)
        {
            // Gia' presente (stessa chiave): nessun errore, nessun giro e nessun punto in piu'.
            return;
        }

        // 3) Giri e punti solo per un'attivita' nuova, nella stessa transazione.
        if (data.Laps.Count > 0)
        {
            db.Laps.AddRange(data.Laps.Select(lap => new ActivityLap
            {
                Id = Guid.NewGuid(),
                ActivityId = values.Id,
                StartTime = lap.StartTime.ToUniversalTime(),
                ElapsedSeconds = lap.ElapsedSeconds,
                DistanceMeters = lap.DistanceMeters,
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await streamWriter.WriteAsync(values.Id, data.StartTime, data.Stream, ct);
    }

    /// <summary>
    /// SQL dell'<c>INSERT</c> costruito dai metadati del modello EF (schema, tabella e colonne non sono scritti a mano) e messo in
    /// cache: il modello e' lo stesso per tutta la vita del processo. I parametri si chiamano come le proprieta'.
    /// </summary>
    private static string InsertSql(ActivitiesDbContext db)
    {
        if (_insertSql is { } cached)
        {
            return cached;
        }

        var entity = db.Model.FindEntityType(typeof(Activity))
            ?? throw new InvalidOperationException($"{nameof(Activity)} non e' mappata nel modello di {nameof(ActivitiesDbContext)}.");
        var tableName = entity.GetTableName() ?? throw new InvalidOperationException($"{nameof(Activity)} non e' mappata su una tabella.");
        var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();

        string Column(string propertyName)
        {
            IProperty property = entity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"Proprieta' {propertyName} non mappata.");
            return sql.DelimitIdentifier(
                property.GetColumnName(store) ?? throw new InvalidOperationException($"Proprieta' {propertyName} senza colonna."));
        }

        var table = sql.DelimitIdentifier(tableName, entity.GetSchema());
        _insertSql =
            $"INSERT INTO {table} ({string.Join(", ", InsertColumns.Select(c => Column(c.Property)))}) "
            + $"VALUES ({string.Join(", ", InsertColumns.Select(c => "@" + c.Property))}) "
            + $"ON CONFLICT ({string.Join(", ", ConflictProperties.Select(Column))}) DO NOTHING";
        return _insertSql;
    }
}
