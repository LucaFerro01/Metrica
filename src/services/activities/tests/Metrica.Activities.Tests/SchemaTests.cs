using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Metrica.Contracts;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Schema prodotto dalla migrazione iniziale, confrontato con "Schema changes" di T11 (REQ-016, REQ-024):
/// avvio con migrazioni, forma esatta di tabelle, colonne, indici e vincoli, hypertable, nessun oggetto fuori dallo schema
/// <c>activities</c>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class SchemaTests(TimescaleFixture db, NatsFixture nats)
{
    private sealed record Column(string Name, string Type, string Nullable, string? Default);

    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        // L'host in Development applica le migrazioni all'avvio: dopo Start lo schema esiste.
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();

        var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<List<T>> QueryAsync<T>(NpgsqlConnection connection, string sql, Func<NpgsqlDataReader, T> map,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    [Fact]
    public async Task Startup_AppliesMigrations_AndCreatesOnlyThePlannedTablesInTheServiceSchema()
    {
        await using var connection = await OpenAdminAsync();

        var tables = await QueryAsync(connection,
            "SELECT table_name || ':' || table_type FROM information_schema.tables WHERE table_schema = 'activities' ORDER BY table_name COLLATE \"C\"",
            r => r.GetString(0));

        Assert.Equal(
            [
                "__EFMigrationsHistory:BASE TABLE",
                "activities:BASE TABLE",
                "activity_laps:BASE TABLE",
                "activity_stream:BASE TABLE",
                "inbox:BASE TABLE",
            ],
            tables);

        var sequences = await QueryAsync(connection,
            "SELECT relname FROM pg_class WHERE relnamespace = 'activities'::regnamespace AND relkind = 'S'",
            r => r.GetString(0));
        Assert.Empty(sequences);

        var routines = await QueryAsync(connection,
            "SELECT proname FROM pg_proc WHERE pronamespace = 'activities'::regnamespace", r => r.GetString(0));
        Assert.Empty(routines);
    }

    [Fact]
    public async Task Startup_CreatesNothingInThePublicSchema()
    {
        await using var connection = await OpenAdminAsync();

        // Relazioni di "public" che non appartengono a un'estensione (quelle di timescaledb sono preesistenti).
        var relations = await QueryAsync(connection,
            """
            SELECT c.relname
            FROM pg_class c
            WHERE c.relnamespace = 'public'::regnamespace
              AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'e')
            """,
            r => r.GetString(0));

        Assert.Empty(relations);
    }

    [Fact]
    public async Task MigrationsHistory_LivesInTheServiceSchema_WithTheInitialMigrationOnly()
    {
        await using var connection = await OpenAdminAsync();

        var migrations = await QueryAsync(connection,
            "SELECT migration_id FROM activities.\"__EFMigrationsHistory\"", r => r.GetString(0));

        var applied = Assert.Single(migrations);
        Assert.Matches(@"^\d{14}_InitialActivitiesSchema$", applied); // nessun prefisso "US" (DEC-011)
    }

    [Fact]
    public async Task ActivitiesTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "activities");

        Assert.Equal(
            Sorted(
                new Column("id", "uuid", "NO", null),
                new Column("user_id", "uuid", "NO", null),
                new Column("source", "text", "NO", null),
                new Column("external_id", "text", "NO", null),
                new Column("sport", "text", "NO", null),
                new Column("start_time", "timestamp with time zone", "NO", null),
                new Column("elapsed_seconds", "double precision", "NO", null),
                new Column("moving_seconds", "double precision", "NO", null),
                new Column("distance_meters", "double precision", "NO", null),
                new Column("elevation_gain_meters", "double precision", "YES", null),
                new Column("avg_heart_rate", "double precision", "YES", null),
                new Column("max_heart_rate", "double precision", "YES", null),
                new Column("avg_power_watts", "double precision", "YES", null),
                new Column("avg_cadence", "double precision", "YES", null),
                new Column("created_at", "timestamp with time zone", "NO", null)),
            Sorted(columns));
    }

    [Fact]
    public async Task ActivityLapsTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "activity_laps");

        Assert.Equal(
            Sorted(
                new Column("id", "uuid", "NO", null),
                new Column("activity_id", "uuid", "NO", null),
                new Column("start_time", "timestamp with time zone", "NO", null),
                new Column("elapsed_seconds", "double precision", "NO", null),
                new Column("distance_meters", "double precision", "NO", null)),
            Sorted(columns));
    }

    [Fact]
    public async Task ActivityStreamTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "activity_stream");

        Assert.Equal(
            Sorted(
                new Column("activity_id", "uuid", "NO", null),
                new Column("time", "timestamp with time zone", "NO", null),
                new Column("latitude", "double precision", "YES", null),
                new Column("longitude", "double precision", "YES", null),
                new Column("altitude_meters", "double precision", "YES", null),
                new Column("heart_rate", "double precision", "YES", null),
                new Column("power_watts", "double precision", "YES", null),
                new Column("cadence", "double precision", "YES", null),
                new Column("speed_mps", "double precision", "YES", null)),
            Sorted(columns));
    }

    [Fact]
    public async Task InboxTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "inbox");

        Assert.Equal(
            Sorted(
                new Column("message_id", "uuid", "NO", null),
                new Column("consumer", "text", "NO", null),
                new Column("processed_at", "timestamp with time zone", "NO", null)),
            Sorted(columns));
    }

    [Fact]
    public async Task Indexes_AreExactlyThePlannedOnes()
    {
        await using var connection = await OpenAdminAsync();

        var indexes = await QueryAsync(connection,
            """
            SELECT tablename, indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'activities' AND tablename IN ('activities', 'activity_laps', 'activity_stream', 'inbox')
            ORDER BY tablename COLLATE "C", indexname COLLATE "C"
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        Assert.Equal(
            [
                ("activities", "ix_activities_user_start",
                    "CREATE INDEX ix_activities_user_start ON activities.activities USING btree (user_id, start_time DESC)"),
                ("activities", "pk_activities",
                    "CREATE UNIQUE INDEX pk_activities ON activities.activities USING btree (id)"),
                ("activities", "ux_activities_user_source_external",
                    "CREATE UNIQUE INDEX ux_activities_user_source_external ON activities.activities USING btree (user_id, source, external_id)"),
                ("activity_laps", "ix_activity_laps_activity_id",
                    "CREATE INDEX ix_activity_laps_activity_id ON activities.activity_laps USING btree (activity_id)"),
                ("activity_laps", "pk_activity_laps",
                    "CREATE UNIQUE INDEX pk_activity_laps ON activities.activity_laps USING btree (id)"),
                // Indice di TimescaleDB: lo crea da se' create_hypertable() (create_default_indexes), non e' dichiarato dalla
                // migrazione. Non e' univoco, quindi non contraddice "nessuna chiave primaria / nessun vincolo di unicita'".
                ("activity_stream", "activity_stream_time_idx",
                    "CREATE INDEX activity_stream_time_idx ON activities.activity_stream USING btree (\"time\" DESC)"),
                ("activity_stream", "ix_activity_stream_activity_time",
                    "CREATE INDEX ix_activity_stream_activity_time ON activities.activity_stream USING btree (activity_id, \"time\")"),
                ("inbox", "pk_inbox",
                    "CREATE UNIQUE INDEX pk_inbox ON activities.inbox USING btree (consumer, message_id)"),
            ],
            indexes);
    }

    [Fact]
    public async Task Constraints_AreOnlyTheNamedKeys_ActivityStreamHasNoPrimaryKey_AndTheLapForeignKeyCascades()
    {
        await using var connection = await OpenAdminAsync();

        // Le versioni recenti di PostgreSQL registrano anche i NOT NULL in pg_constraint ('n'): non sono vincoli aggiunti dalla migrazione.
        var constraints = await QueryAsync(connection,
            """
            SELECT conname, contype::text, conrelid::regclass::text, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE connamespace = 'activities'::regnamespace AND contype <> 'n'
            ORDER BY conname COLLATE "C"
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

        Assert.Equal(
            [
                ("fk_activity_laps_activities_activity_id", "f", "activities.activity_laps",
                    "FOREIGN KEY (activity_id) REFERENCES activities.activities(id) ON DELETE CASCADE"),
                ("pk___ef_migrations_history", "p", "activities.\"__EFMigrationsHistory\"", "PRIMARY KEY (migration_id)"),
                ("pk_activities", "p", "activities.activities", "PRIMARY KEY (id)"),
                ("pk_activity_laps", "p", "activities.activity_laps", "PRIMARY KEY (id)"),
                ("pk_inbox", "p", "activities.inbox", "PRIMARY KEY (consumer, message_id)"),
            ],
            constraints);
        Assert.DoesNotContain(constraints, c => c.Item3 == "activities.activity_stream");
    }

    [Fact]
    public async Task ActivityStream_IsAHypertableOnTheTimeColumn()
    {
        await using var connection = await OpenAdminAsync();

        var hypertables = await QueryAsync(connection,
            """
            SELECT hypertable_schema, hypertable_name, num_dimensions::int
            FROM timescaledb_information.hypertables
            WHERE hypertable_schema = 'activities'
            """,
            r => (r.GetString(0), r.GetString(1), r.GetInt32(2)));
        Assert.Equal([("activities", "activity_stream", 1)], hypertables);

        var dimensions = await QueryAsync(connection,
            """
            SELECT column_name, dimension_type
            FROM timescaledb_information.dimensions
            WHERE hypertable_schema = 'activities' AND hypertable_name = 'activity_stream'
            """,
            r => (r.GetString(0), r.GetString(1)));
        Assert.Equal([("time", "Time")], dimensions);
    }

    [Fact]
    public async Task OnlyActivityStream_IsAHypertable()
    {
        await using var connection = await OpenAdminAsync();

        var tables = await QueryAsync(connection,
            "SELECT hypertable_name FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities' ORDER BY 1",
            r => r.GetString(0));

        Assert.Equal(["activity_stream"], tables);
    }

    [Fact]
    public async Task UniqueIndex_RejectsTheSameUserSourceAndExternalId_ButNotAnotherUserOrSource()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        var (userId, otherUserId, externalId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"));

        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();

        await InsertActivityAsync(connection, Guid.NewGuid(), userId, "file", externalId);
        await InsertActivityAsync(connection, Guid.NewGuid(), otherUserId, "file", externalId);   // altro utente: ammesso
        await InsertActivityAsync(connection, Guid.NewGuid(), userId, "strava", externalId);      // altra fonte: ammesso
        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => InsertActivityAsync(connection, Guid.NewGuid(), userId, "file", externalId));

        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("ux_activities_user_source_external", duplicate.ConstraintName);
    }

    [Fact]
    public async Task InboxPrimaryKey_IsComposite_SameMessageCanBeConsumedByTwoConsumersButNotTwiceByOne()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        var messageId = Guid.NewGuid();
        var (first, second) = ($"t11-a-{Guid.NewGuid():N}", $"t11-b-{Guid.NewGuid():N}");

        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();

        await InsertInboxAsync(connection, first, messageId);
        await InsertInboxAsync(connection, second, messageId);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertInboxAsync(connection, first, messageId));

        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("pk_inbox", duplicate.ConstraintName);
    }

    [Fact]
    public async Task DeletingAnActivity_DeletesItsLaps()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        var activityId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();
        await InsertActivityAsync(connection, activityId, Guid.NewGuid(), "file", Guid.NewGuid().ToString("N"));
        await Execute(connection,
            "INSERT INTO activities.activity_laps (id, activity_id, start_time, elapsed_seconds, distance_meters) VALUES (gen_random_uuid(), @a, now(), 60, 200)",
            ("a", activityId));
        await Execute(connection,
            "INSERT INTO activities.activity_laps (id, activity_id, start_time, elapsed_seconds, distance_meters) VALUES (gen_random_uuid(), @a, now(), 61, 201)",
            ("a", activityId));
        Assert.Equal(2L, await Scalar(connection, "SELECT count(*) FROM activities.activity_laps WHERE activity_id = @a", ("a", activityId)));

        await Execute(connection, "DELETE FROM activities.activities WHERE id = @a", ("a", activityId));

        Assert.Equal(0L, await Scalar(connection, "SELECT count(*) FROM activities.activity_laps WHERE activity_id = @a", ("a", activityId)));
    }

    [Fact]
    public async Task LapForeignKey_RejectsALapOfAnUnknownActivity()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection,
            "INSERT INTO activities.activity_laps (id, activity_id, start_time, elapsed_seconds, distance_meters) VALUES (gen_random_uuid(), @a, now(), 1, 1)",
            ("a", Guid.NewGuid())));

        Assert.Equal("23503", error.SqlState);
    }

    [Theory]
    [InlineData(Sport.Run, "run")]
    [InlineData(Sport.Ride, "ride")]
    [InlineData(Sport.Swim, "swim")]
    [InlineData(Sport.Other, "other")]
    public async Task Sport_IsStoredAsLowercaseText_AndReadBackThroughEfCore(Sport sport, string expectedText)
    {
        await MigratedDatabase.EnsureAsync(db);
        var (id, userId) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var context = MigratedDatabase.NewContext(db))
        {
            context.Activities.Add(new Activity
            {
                Id = id,
                UserId = userId,
                Source = "file",
                ExternalId = Guid.NewGuid().ToString("N"),
                Sport = sport,
                StartTime = DateTimeOffset.UtcNow,
                ElapsedSeconds = 10,
                MovingSeconds = 9,
                DistanceMeters = 100,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT sport FROM activities.activities WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        Assert.Equal(expectedText, (string?)await command.ExecuteScalarAsync());

        await using var reader = MigratedDatabase.NewContext(db);
        Assert.Equal(sport, (await reader.Activities.AsNoTracking().SingleAsync(a => a.Id == id)).Sport);
        // Il filtro per sport (T13) deve tradursi in SQL sul testo minuscolo.
        Assert.Equal(1, await reader.Activities.CountAsync(a => a.UserId == userId && a.Sport == sport));
    }

    [Fact]
    public async Task Model_HasNoPendingChanges_AgainstTheMigrationSnapshot()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<ActivitiesDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    private static List<Column> Sorted(params Column[] columns) => [.. columns.OrderBy(c => c.Name, StringComparer.Ordinal)];

    private static List<Column> Sorted(IEnumerable<Column> columns) => [.. columns.OrderBy(c => c.Name, StringComparer.Ordinal)];

    private static async Task<List<Column>> ColumnsAsync(NpgsqlConnection connection, string table) =>
        await QueryAsync(connection,
            """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'activities' AND table_name = @table
            """,
            r => new Column(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)),
            ("table", table));

    private static Task InsertActivityAsync(NpgsqlConnection connection, Guid id, Guid userId, string source, string externalId) =>
        Execute(connection,
            """
            INSERT INTO activities.activities (id, user_id, source, external_id, sport, start_time, elapsed_seconds, moving_seconds, distance_meters, created_at)
            VALUES (@id, @user, @source, @external, 'run', now(), 10, 9, 100, now())
            """,
            ("id", id), ("user", userId), ("source", source), ("external", externalId));

    private static Task InsertInboxAsync(NpgsqlConnection connection, string consumer, Guid messageId) =>
        Execute(connection,
            "INSERT INTO activities.inbox (message_id, consumer, processed_at) VALUES (@m, @c, now())",
            ("m", messageId), ("c", consumer));

    private static async Task Execute(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync();
    }
}
