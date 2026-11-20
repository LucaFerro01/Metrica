using Npgsql;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Support;

/// <summary>Riga di <c>activities.activities</c>.</summary>
internal sealed record ActivityRow(
    Guid Id, Guid UserId, string Source, string ExternalId, string Sport, DateTime StartTime, double ElapsedSeconds,
    double MovingSeconds, double DistanceMeters, double? ElevationGainMeters, double? AvgHeartRate, double? MaxHeartRate,
    double? AvgPowerWatts, double? AvgCadence);

/// <summary>Riga di <c>ingestion.outbox</c> (senza payload).</summary>
internal sealed record OutboxRow(Guid Id, string Subject, string Type, DateTime? PublishedAt, int Attempts);

/// <summary>Punto di <c>activities.activity_stream</c>.</summary>
internal sealed record StreamRow(DateTime Time, double? Latitude, double? Longitude, double? Altitude, double? HeartRate, double? Power, double? Cadence, double? Speed);

/// <summary>
/// Letture e azzeramenti del database come SUPERUTENTE: verifica indipendente da quanto fa il codice sotto test (le scritture
/// dei servizi usano i loro ruoli veri).
/// </summary>
internal sealed class Database(TimescaleFixture fixture)
{
    public TimescaleFixture Fixture { get; } = fixture;

    /// <summary>
    /// Svuota le tabelle dei due schemi (se esistono gia': alla prima esecuzione le crea la migrazione all'avvio degli host).
    /// Sicuro solo perche' la collection non gira in parallelo e gli host dello scenario non sono ancora avviati.
    /// </summary>
    public Task ResetAsync() => ExecuteAsync(
        """
        DO $$
        BEGIN
            -- Tabella di prova del test sugli schemi: non deve sopravvivere a un test fallito.
            DROP TABLE IF EXISTS ingestion.it_isolation_probe;
            DROP TABLE IF EXISTS activities.it_isolation_probe;
            IF to_regclass('ingestion.outbox') IS NOT NULL THEN
                TRUNCATE ingestion.outbox, ingestion.uploads;
            END IF;
            IF to_regclass('activities.activities') IS NOT NULL THEN
                -- Vincolo aggiunto da un test sulla DLQ: non deve sopravvivere a un test fallito.
                ALTER TABLE activities.activity_laps DROP CONSTRAINT IF EXISTS it_forbidden_lap;
                TRUNCATE activities.activity_stream, activities.activity_laps, activities.inbox, activities.activities;
            END IF;
        END
        $$;
        """);

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task<IReadOnlyList<ActivityRow>> ActivitiesOfAsync(Guid userId)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, source, external_id, sport, start_time, elapsed_seconds, moving_seconds, distance_meters,
                   elevation_gain_meters, avg_heart_rate, max_heart_rate, avg_power_watts, avg_cadence
            FROM activities.activities WHERE user_id = @u ORDER BY start_time DESC, id
            """,
            connection);
        command.Parameters.AddWithValue("u", userId);
        var rows = new List<ActivityRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ActivityRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<DateTime>(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12), Nullable(reader, 13)));
        }

        return rows;
    }

    public Task<long> ActivityCountAsync() => ScalarAsync<long>("SELECT count(*) FROM activities.activities");

    public Task<long> StreamPointCountAsync(Guid activityId) =>
        ScalarAsync<long>("SELECT count(*) FROM activities.activity_stream WHERE activity_id = @a", ("a", activityId));

    public Task<long> InboxCountAsync() => ScalarAsync<long>("SELECT count(*) FROM activities.inbox");

    public Task<long> UploadCountAsync(Guid userId) =>
        ScalarAsync<long>("SELECT count(*) FROM ingestion.uploads WHERE user_id = @u", ("u", userId));

    public Task<long> UploadCountAsync() => ScalarAsync<long>("SELECT count(*) FROM ingestion.uploads");

    public async Task<IReadOnlyList<OutboxRow>> OutboxAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, subject, type, published_at, attempts FROM ingestion.outbox ORDER BY created_at, id", connection);
        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3), reader.GetInt32(4)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<StreamRow>> StreamAsync(Guid activityId, IEnumerable<DateTime> times)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT time, latitude, longitude, altitude_meters, heart_rate, power_watts, cadence, speed_mps
            FROM activities.activity_stream WHERE activity_id = @a AND time = ANY(@t) ORDER BY time
            """,
            connection);
        command.Parameters.AddWithValue("a", activityId);
        command.Parameters.AddWithValue("t", times.Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToArray());
        var rows = new List<StreamRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StreamRow(
                reader.GetFieldValue<DateTime>(0), Nullable(reader, 1), Nullable(reader, 2), Nullable(reader, 3),
                Nullable(reader, 4), Nullable(reader, 5), Nullable(reader, 6), Nullable(reader, 7)));
        }

        return rows;
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(Fixture.AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static double? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
