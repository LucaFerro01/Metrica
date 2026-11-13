using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// Schema prodotto dalla migrazione iniziale, confrontato con "Schema changes" di T09 (REQ-024, REQ-012):
/// avvio con migrazioni, forma esatta di tabelle, indici e vincoli, nessun oggetto fuori dallo schema <c>ingestion</c>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class SchemaTests(TimescaleFixture db, NatsFixture nats)
{
    private async Task<NpgsqlConnection> OpenAdminAsync()
    {
        // L'host in Development applica le migrazioni all'avvio: dopo Start lo schema esiste.
        await using var host = IngestionHost.Development(db, nats);
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
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'ingestion' ORDER BY table_name COLLATE \"C\"",
            r => r.GetString(0));

        Assert.Equal(["__EFMigrationsHistory", "outbox", "uploads"], tables);

        var sequences = await QueryAsync(connection,
            "SELECT relname FROM pg_class WHERE relnamespace = 'ingestion'::regnamespace AND relkind = 'S'",
            r => r.GetString(0));
        Assert.Empty(sequences);
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
            "SELECT migration_id FROM ingestion.\"__EFMigrationsHistory\"", r => r.GetString(0));

        var applied = Assert.Single(migrations);
        Assert.EndsWith("_InitialIngestionSchema", applied);
        Assert.Matches(@"^\d{14}_InitialIngestionSchema$", applied); // nessun prefisso "US" (DEC-011)
    }

    [Fact]
    public async Task UploadsTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "uploads");

        Assert.Equal(
            new Dictionary<string, (string Type, string Nullable, string? Default)>
            {
                ["id"] = ("uuid", "NO", null),
                ["user_id"] = ("uuid", "NO", null),
                ["source"] = ("text", "NO", null),
                ["external_id"] = ("text", "NO", null),
                ["file_name"] = ("text", "NO", null),
                ["size_bytes"] = ("bigint", "NO", null),
                ["created_at"] = ("timestamp with time zone", "NO", null),
            }.OrderBy(p => p.Key, StringComparer.Ordinal),
            columns.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OutboxTable_HasExactlyThePlannedColumns()
    {
        await using var connection = await OpenAdminAsync();

        var columns = await ColumnsAsync(connection, "outbox");

        Assert.Equal(
            new Dictionary<string, (string Type, string Nullable, string? Default)>
            {
                ["id"] = ("uuid", "NO", null),
                ["subject"] = ("text", "NO", null),
                ["type"] = ("text", "NO", null),
                ["payload"] = ("bytea", "NO", null),
                ["created_at"] = ("timestamp with time zone", "NO", null),
                ["published_at"] = ("timestamp with time zone", "YES", null),
                ["attempts"] = ("integer", "NO", "0"),
                ["last_error"] = ("text", "YES", null),
            }.OrderBy(p => p.Key, StringComparer.Ordinal),
            columns.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Indexes_AreExactlyThePlannedOnes()
    {
        await using var connection = await OpenAdminAsync();

        var indexes = await QueryAsync(connection,
            """
            SELECT tablename, indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = 'ingestion' AND tablename IN ('uploads', 'outbox')
            ORDER BY tablename COLLATE "C", indexname COLLATE "C"
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2)));

        Assert.Equal(
            [
                ("outbox", "ix_outbox_unpublished",
                    "CREATE INDEX ix_outbox_unpublished ON ingestion.outbox USING btree (created_at) WHERE (published_at IS NULL)"),
                ("outbox", "pk_outbox",
                    "CREATE UNIQUE INDEX pk_outbox ON ingestion.outbox USING btree (id)"),
                ("uploads", "pk_uploads",
                    "CREATE UNIQUE INDEX pk_uploads ON ingestion.uploads USING btree (id)"),
                ("uploads", "ux_uploads_user_source_external",
                    "CREATE UNIQUE INDEX ux_uploads_user_source_external ON ingestion.uploads USING btree (user_id, source, external_id)"),
            ],
            indexes);
    }

    [Fact]
    public async Task Constraints_AreOnlyTheNamedPrimaryKeys()
    {
        await using var connection = await OpenAdminAsync();

        // PG18 registra anche i NOT NULL in pg_constraint ('n'): non sono vincoli aggiunti dalla migrazione.
        var constraints = await QueryAsync(connection,
            """
            SELECT conname, contype::text, conrelid::regclass::text, pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE connamespace = 'ingestion'::regnamespace AND contype <> 'n'
            ORDER BY conname COLLATE "C"
            """,
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));

        Assert.Equal(
            [
                ("pk___ef_migrations_history", "p", "ingestion.\"__EFMigrationsHistory\"", "PRIMARY KEY (migration_id)"),
                ("pk_outbox", "p", "ingestion.outbox", "PRIMARY KEY (id)"),
                ("pk_uploads", "p", "ingestion.uploads", "PRIMARY KEY (id)"),
            ],
            constraints);
    }

    [Fact]
    public async Task UniqueIndex_RejectsTheSameUserSourceAndExternalId_ButNotAnotherUser()
    {
        await using var host = IngestionHost.Development(db, nats);
        host.Start();
        var (userId, otherUserId, externalId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("N"));

        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("ingestion"));
        await connection.OpenAsync();

        await InsertUploadAsync(connection, userId, "file", externalId);
        await InsertUploadAsync(connection, otherUserId, "file", externalId);   // altro utente: ammesso
        await InsertUploadAsync(connection, userId, "strava", externalId);      // altra fonte: ammesso
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => InsertUploadAsync(connection, userId, "file", externalId));

        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("ux_uploads_user_source_external", duplicate.ConstraintName);
    }

    [Fact]
    public async Task Model_HasNoPendingChanges_AgainstTheMigrationSnapshot()
    {
        await using var host = IngestionHost.Development(db, nats);
        host.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();

        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    private static async Task<Dictionary<string, (string Type, string Nullable, string? Default)>> ColumnsAsync(
        NpgsqlConnection connection, string table)
    {
        var rows = await QueryAsync(connection,
            """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'ingestion' AND table_name = @table
            """,
            r => (r.GetString(0), (r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3))),
            ("table", table));
        return rows.ToDictionary(r => r.Item1, r => r.Item2);
    }

    private static async Task InsertUploadAsync(NpgsqlConnection connection, Guid userId, string source, string externalId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ingestion.uploads (id, user_id, source, external_id, file_name, size_bytes, created_at)
            VALUES (@id, @user, @source, @external, 'corsa.gpx', 10, now())
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("source", source);
        command.Parameters.AddWithValue("external", externalId);
        await command.ExecuteNonQueryAsync();
    }
}
