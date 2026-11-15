using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Legge lo stream di un'attivita' da <c>activities.activity_stream</c> ridotto a passo costante (REQ-018, AC-011): al massimo
/// <c>maxPoints</c> punti, sempre con il primo e l'ultimo, indici deterministici e ordinati per tempo.
/// </summary>
/// <remarks>
/// <para><b>Riduzione nel database.</b> Una prima query conta i punti (N) e, per ogni colonna, se ha almeno un valore non nullo.
/// Se N &lt;= maxPoints si leggono tutti i punti; altrimenti il database numera i punti in ordine di tempo
/// (<c>row_number()</c>) e restituisce SOLO quelli con indice in <see cref="SelectIndices"/>: dal database escono al massimo
/// maxPoints righe, mai le 100.000 dell'attivita'. Indici: <c>round(k*(N-1)/(m-1))</c> per k=0..m-1, m=min(N, maxPoints), in
/// aritmetica intera (meta' verso l'alto); con N-1 &gt;= m-1 il passo e' almeno 1, quindi nessun doppione, e le differenze tra
/// indici consecutivi valgono floor o ceil di (N-1)/(m-1).</para>
/// <para><b>Ordine.</b> Per tempo e, a parita' di istante (il contratto ammette scarti uguali), per <c>ctid</c>: i punti si scrivono
/// con un solo COPY, quindi l'ordine fisico e' quello dello stream e la scelta e' ripetibile tra due richieste.</para>
/// <para><b>Tempo.</b> <c>TimeOffsetSeconds</c> = (time - inizio) in microsecondi interi / 10^6: l'inverso esatto di
/// <see cref="StreamWriter"/> (nessuna somma in virgola mobile), gli scarti scritti tornano identici.</para>
/// <para><b>Colonne.</b> Una colonna senza alcun valore nell'intera attivita' e' <c>null</c> (assente); se ha almeno un valore resta
/// un array, anche se i punti scelti sono tutti nulli. Uno stream senza punti restituisce <c>TimeOffsetSeconds</c> vuoto.</para>
/// <para>SQL parametrizzato, connessione del contesto (ruolo <c>activities_svc</c>), nessun log (REQ-034).</para>
/// </remarks>
public sealed class ActivityStreamReader(ActivitiesDbContext db)
{
    // Colonne di valori nell'ordine di StreamColumns (stessa sorgente dei nomi del COPY di StreamWriter).
    private static readonly string[] ValueColumns = [.. StreamWriter.Columns.Skip(2).Select(c => c.Name)];

    private static readonly string Select = string.Join(", ", ValueColumns.Prepend("time"));

    private static readonly string StatsSql =
        $"SELECT count(*), {string.Join(", ", ValueColumns.Select(c => $"bool_or({c} IS NOT NULL)"))} "
        + $"FROM {StreamWriter.TableName} WHERE activity_id = @id";

    private static readonly string AllSql =
        $"SELECT {Select} FROM {StreamWriter.TableName} WHERE activity_id = @id ORDER BY time, ctid";

    private static readonly string ReducedSql =
        $"SELECT {Select} FROM (SELECT {Select}, row_number() OVER (ORDER BY time, ctid) - 1 AS idx "
        + $"FROM {StreamWriter.TableName} WHERE activity_id = @id) AS numbered WHERE idx = ANY(@indices) ORDER BY idx";

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>Stream dell'attivita' ridotto ad al massimo <paramref name="maxPoints"/> punti (almeno 2).</summary>
    public async Task<StreamColumns> ReadAsync(Guid activityId, DateTimeOffset startTime, int maxPoints, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, ActivitiesApiOptions.MinMaxPoints);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var (count, present) = await ReadStatsAsync(connection, activityId, ct);
            if (count == 0)
            {
                return new StreamColumns([], null, null, null, null, null, null, null);
            }

            var indices = count <= maxPoints ? null : SelectIndices(count, maxPoints);
            await using var command = new NpgsqlCommand(indices is null ? AllSql : ReducedSql, connection);
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = activityId });
            if (indices is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("indices", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = indices });
            }

            var expected = indices?.Length ?? (int)count;
            var offsets = new List<double>(expected);
            var columns = present.Select(p => p ? new List<double?>(expected) : null).ToArray();
            var startTicks = startTime.UtcTicks;

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            while (await reader.ReadAsync(ct))
            {
                var time = reader.GetFieldValue<DateTime>(0);
                offsets.Add((time.Ticks - startTicks) / TicksPerMicrosecond / 1_000_000d);
                for (var c = 0; c < columns.Length; c++)
                {
                    columns[c]?.Add(await reader.IsDBNullAsync(c + 1, ct) ? null : reader.GetDouble(c + 1));
                }
            }

            return new StreamColumns(offsets, columns[0], columns[1], columns[2], columns[3], columns[4], columns[5], columns[6]);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Indici (da 0) dei punti scelti tra <paramref name="count"/>: <c>round(k*(count-1)/(m-1))</c> per k=0..m-1 con
    /// m=min(count, maxPoints), meta' verso l'alto, in aritmetica intera. Il primo e' sempre 0 e l'ultimo count-1.
    /// </summary>
    public static long[] SelectIndices(long count, int maxPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, ActivitiesApiOptions.MinMaxPoints);

        var m = Math.Min(count, maxPoints);
        if (m <= 1)
        {
            return m == 0 ? [] : [0];
        }

        var indices = new long[m];
        var span = count - 1;
        var steps = m - 1;
        for (long k = 0; k < m; k++)
        {
            indices[k] = ((2 * k * span) + steps) / (2 * steps);
        }

        return indices;
    }

    private static async Task<(long Count, bool[] Present)> ReadStatsAsync(NpgsqlConnection connection, Guid activityId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(StatsSql, connection);
        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = activityId });
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        await reader.ReadAsync(ct);
        var present = new bool[ValueColumns.Length];
        for (var c = 0; c < present.Length; c++)
        {
            present[c] = !reader.IsDBNull(c + 1) && reader.GetBoolean(c + 1);
        }

        return (reader.GetInt64(0), present);
    }
}
