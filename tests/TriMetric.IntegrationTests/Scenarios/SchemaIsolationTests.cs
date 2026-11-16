using System.Net;
using Npgsql;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>
/// AC-008 (REQ-023): <c>activities_svc</c> non legge ne' scrive nello schema <c>ingestion</c> e <c>ingestion_svc</c> non legge ne'
/// scrive in <c>activities</c> (tabelle, hypertable, inbox, chunk), con i ruoli e i grant dello script di init REALE. Ogni ruolo
/// opera invece sul proprio schema (controllo positivo con le stesse istruzioni: senza, il test passerebbe a vuoto).
/// </summary>
[Collection(E2eCollection.Name)]
public sealed class SchemaIsolationTests(TimescaleFixture db, NatsFixture nats)
{
    private const string InsufficientPrivilege = "42501";

    [Fact]
    public async Task Ac008_EachRoleWorksOnItsOwnSchema_AndIsDeniedOnTheOther()
    {
        // I due schemi hanno tabelle e dati veri: si caricano un file e un evento.
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        await DropProbeTablesAsync(system);   // residuo di una prova interrotta
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 50, Start = new DateTimeOffset(2024, 5, 3, 7, 0, 0, TimeSpan.Zero) }.Build();
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(file));
        await system.Api.WaitForActivityAsync(file.Sha256);

        var ingestionTables = await TablesOfAsync(system, "ingestion");
        var activitiesTables = await TablesOfAsync(system, "activities");
        Assert.Superset(new HashSet<string>(["uploads", "outbox"]), ingestionTables.ToHashSet());
        Assert.Superset(new HashSet<string>(["activities", "activity_laps", "activity_stream", "inbox"]), activitiesTables.ToHashSet());

        // Positivo: ogni ruolo legge e scrive nel proprio schema.
        await AssertAllowedAsync("activities", "activities", activitiesTables);
        await AssertAllowedAsync("ingestion", "ingestion", ingestionTables);
        Assert.True(await CountAsync("activities", "activities.activities") >= 1);
        Assert.True(await CountAsync("ingestion", "ingestion.uploads") >= 1);

        // Negativo: stesse istruzioni, schema dell'altro servizio.
        await AssertDeniedAsync("activities", "ingestion", ingestionTables);
        await AssertDeniedAsync("ingestion", "activities", activitiesTables);
    }

    [Fact]
    public async Task Ac008_HypertableChunks_AreNotReadableByTheOtherRole()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 50, Start = new DateTimeOffset(2024, 5, 3, 7, 0, 0, TimeSpan.Zero) }.Build();
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(file));
        await system.Api.WaitForActivityAsync(file.Sha256);

        // I punti stanno in un chunk fisico della hypertable (schema interno di TimescaleDB): anche quello non e' accessibile
        // all'altro ruolo, che altrimenti potrebbe leggere i dati di activities aggirando lo schema.
        await using var admin = await system.Db.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT format('%I.%I', chunk_schema, chunk_name) FROM timescaledb_information.chunks WHERE hypertable_schema = 'activities' AND hypertable_name = 'activity_stream'",
            admin);
        var chunks = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                chunks.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            await AssertDeniedAsync("ingestion", $"SELECT count(*) FROM {chunk}");
            await AssertDeniedAsync("ingestion", $"INSERT INTO {chunk} SELECT * FROM {chunk} WHERE false");
            await AssertDeniedAsync("ingestion", $"DELETE FROM {chunk}");
        }
    }

    private static async Task DropProbeTablesAsync(EndToEndSystem system)
    {
        await system.Db.ExecuteAsync("DROP TABLE IF EXISTS ingestion.it_isolation_probe");
        await system.Db.ExecuteAsync("DROP TABLE IF EXISTS activities.it_isolation_probe");
    }

    private async Task AssertAllowedAsync(string role, string schema, IReadOnlyList<string> tables)
    {
        foreach (var statement in await StatementsAsync(schema, tables))
        {
            await using var connection = await OpenAsync(role);
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();   // se il ruolo non avesse il permesso lancerebbe 42501
        }

        // La tabella creata come prova non resta nel database condiviso.
        await using var cleanup = await OpenAsync(role);
        await using var drop = new NpgsqlCommand($"DROP TABLE {schema}.it_isolation_probe", cleanup);
        await drop.ExecuteNonQueryAsync();
    }

    private async Task AssertDeniedAsync(string role, string schema, IReadOnlyList<string> tables)
    {
        foreach (var statement in await StatementsAsync(schema, tables))
        {
            await AssertDeniedAsync(role, statement);
        }
    }

    private async Task AssertDeniedAsync(string role, string statement)
    {
        await using var connection = await OpenAsync(role);
        await using var command = new NpgsqlCommand(statement, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.True(
            error.SqlState == InsufficientPrivilege,
            $"{role}: «{statement}» doveva dare 42501 (permesso negato), ha dato {error.SqlState}: {error.MessageText}");
    }

    /// <summary>
    /// Per ogni tabella: lettura (SELECT), scrittura (INSERT, UPDATE, DELETE senza righe interessate: il permesso si verifica
    /// comunque) e, per lo schema, creazione di una tabella. Le stesse istruzioni servono per il controllo positivo e per quello negativo.
    /// </summary>
    private async Task<IReadOnlyList<string>> StatementsAsync(string schema, IReadOnlyList<string> tables)
    {
        var statements = new List<string>();
        foreach (var table in tables)
        {
            var name = $"{schema}.\"{table}\"";
            var column = await FirstColumnAsync(schema, table);
            statements.Add($"SELECT count(*) FROM {name}");
            statements.Add($"SELECT * FROM {name} LIMIT 1");
            statements.Add($"INSERT INTO {name} SELECT * FROM {name} WHERE false");
            statements.Add($"UPDATE {name} SET \"{column}\" = \"{column}\" WHERE false");
            statements.Add($"DELETE FROM {name} WHERE false");
        }

        statements.Add($"CREATE TABLE {schema}.it_isolation_probe (a int)");
        return statements;
    }

    private async Task<IReadOnlyList<string>> TablesOfAsync(EndToEndSystem system, string schema)
    {
        await using var connection = await system.Db.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_type = 'BASE TABLE' ORDER BY table_name",
            connection);
        command.Parameters.AddWithValue("s", schema);
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private async Task<string> FirstColumnAsync(string schema, string table)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position LIMIT 1",
            connection);
        command.Parameters.AddWithValue("s", schema);
        command.Parameters.AddWithValue("t", table);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountAsync(string role, string qualifiedTable)
    {
        await using var connection = await OpenAsync(role);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {qualifiedTable}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenAsync(string service)
    {
        var connection = new NpgsqlConnection(db.ConnectionStringFor(service));
        await connection.OpenAsync();
        return connection;
    }
}
