using Npgsql;
using Metrica.Contracts;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>Una riga letta da <c>activities.activity_stream</c>.</summary>
internal sealed record StoredPoint(
    Guid ActivityId, DateTime Time, double? Latitude, double? Longitude, double? Altitude, double? HeartRate, double? Power,
    double? Cadence, double? Speed);

/// <summary>Stream di prova deterministici e lettura/verifica dei punti salvati.</summary>
internal static class StreamTestData
{
    /// <summary>Inizio con una frazione di secondo (0,123456 s), per verificare l'addizione esatta.</summary>
    public static readonly DateTimeOffset StartWithFraction =
        new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);

    /// <summary>
    /// Stream con <paramref name="count"/> punti, offset strettamente crescenti a passo di millisecondi interi
    /// (<c>i * 1000 + (i % 7) * 137</c> ms, come li produce l'arrotondamento a 0,001 s di ingestion) e colonne con alcuni
    /// <c>null</c> dentro la colonna; con <paramref name="allColumns"/> falso la cadenza e l'altitudine sono assenti (colonna null).
    /// </summary>
    public static StreamColumns Build(int count, bool allColumns = true, bool nullsInsideColumns = true)
    {
        var offsets = new double[count];
        var lat = new double?[count];
        var lon = new double?[count];
        var alt = new double?[count];
        var hr = new double?[count];
        var power = new double?[count];
        var cadence = new double?[count];
        var speed = new double?[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = OffsetMilliseconds(i) / 1000.0;
            lat[i] = nullsInsideColumns && i % 11 == 5 ? null : Math.Round(45.0 + i * 0.0000013, 7);
            lon[i] = nullsInsideColumns && i % 11 == 5 ? null : Math.Round(-7.5 - i * 0.0000017, 7);
            alt[i] = Math.Round(-12.5 + (i % 500) * 0.7, 1);
            hr[i] = nullsInsideColumns && i % 10 == 3 ? null : 60 + i % 140;
            power[i] = 100 + i % 900;
            cadence[i] = i % 120;
            speed[i] = Math.Round((i % 3000) / 100.0 + 0.001, 3);
        }

        return new StreamColumns(
            offsets, lat, lon, allColumns ? alt : null, hr, power, allColumns ? cadence : null, speed);
    }

    public static long OffsetMilliseconds(int index) => index * 1000L + (index % 7) * 137;

    public static string ConnectionString(TimescaleFixture db) => db.ConnectionStringFor("activities");

    /// <summary>Tutte le righe dell'attivita' in ordine di tempo (gli offset di <see cref="Build"/> sono strettamente crescenti).</summary>
    public static async Task<List<StoredPoint>> ReadAsync(TimescaleFixture db, Guid activityId)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT activity_id, time, latitude, longitude, altitude_meters, heart_rate, power_watts, cadence, speed_mps
            FROM activities.activity_stream WHERE activity_id = @id ORDER BY time
            """,
            connection);
        command.Parameters.AddWithValue("id", activityId);

        var rows = new List<StoredPoint>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new StoredPoint(
                reader.GetGuid(0), reader.GetFieldValue<DateTime>(1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4),
                Nullable(reader, 5), Nullable(reader, 6), Nullable(reader, 7), Nullable(reader, 8)));
        }

        return rows;
    }

    public static async Task<long> CountAsync(TimescaleFixture db, Guid activityId)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM activities.activity_stream WHERE activity_id = @id", connection);
        command.Parameters.AddWithValue("id", activityId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<long> CountInboxAsync(TimescaleFixture db, string consumer)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM activities.inbox WHERE consumer = @c", connection);
        command.Parameters.AddWithValue("c", consumer);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Confronta ogni riga con lo stream atteso; <c>time</c> deve coincidere esattamente (al microsecondo).</summary>
    public static void AssertEqual(DateTimeOffset start, StreamColumns expected, Guid activityId, IReadOnlyList<StoredPoint> actual)
    {
        Assert.Equal(expected.TimeOffsetSeconds.Count, actual.Count);
        var startTicks = start.UtcTicks;
        for (var i = 0; i < actual.Count; i++)
        {
            var row = actual[i];
            // Le righe sono ordinate per tempo e gli offset di prova sono crescenti: l'indice della riga e' l'indice del punto.
            var expectedTicks = startTicks + OffsetMilliseconds(i) * TimeSpan.TicksPerMillisecond;
            if (row.Time.Ticks != expectedTicks || row.ActivityId != activityId
                || row.Latitude != At(expected.Latitude, i) || row.Longitude != At(expected.Longitude, i)
                || row.Altitude != At(expected.AltitudeMeters, i) || row.HeartRate != At(expected.HeartRate, i)
                || row.Power != At(expected.PowerWatts, i) || row.Cadence != At(expected.Cadence, i)
                || row.Speed != At(expected.SpeedMetersPerSecond, i))
            {
                Assert.Fail($"Riga {i} diversa dall'attesa: tempo {row.Time.Ticks} (atteso {expectedTicks}), "
                    + $"valori {row.Latitude}/{row.Longitude}/{row.Altitude}/{row.HeartRate}/{row.Power}/{row.Cadence}/{row.Speed}.");
            }
        }
    }

    private static double? At(IReadOnlyList<double?>? column, int index) => column?[index];

    private static double? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
