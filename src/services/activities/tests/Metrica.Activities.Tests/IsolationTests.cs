using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Isolamento dei dati tra servizi (REQ-023, AC-008): il ruolo <c>activities_svc</c> vede solo il proprio schema e,
/// viceversa, <c>ingestion_svc</c> non vede lo schema <c>activities</c>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class IsolationTests(TimescaleFixture db, NatsFixture nats)
{
    private static async Task<PostgresException> RunAsFailingAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("SELECT count(*) FROM ingestion.uploads")]
    [InlineData("SELECT count(*) FROM ingestion.outbox")]
    [InlineData("INSERT INTO ingestion.uploads (id) VALUES (gen_random_uuid())")]
    [InlineData("DELETE FROM ingestion.uploads")]
    [InlineData("CREATE TABLE ingestion.t11_intruder (id int)")]
    [InlineData("SELECT count(*) FROM identity.anything")]
    [InlineData("SELECT count(*) FROM analytics.anything")]
    [InlineData("SELECT count(*) FROM training_plan.anything")]
    [InlineData("CREATE TABLE public.t11_intruder (id int)")]
    public async Task ActivitiesRole_CannotReadOrWriteOtherSchemas_NorCreateInPublic(string sql)
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();

        var error = await RunAsFailingAsync(db.ConnectionStringFor("activities"), sql);

        Assert.Equal("42501", error.SqlState); // insufficient_privilege: accesso negato, non "tabella inesistente"
    }

    [Theory]
    [InlineData("SELECT count(*) FROM activities.activities")]
    [InlineData("SELECT count(*) FROM activities.activity_laps")]
    [InlineData("SELECT count(*) FROM activities.activity_stream")]
    [InlineData("SELECT count(*) FROM activities.inbox")]
    [InlineData("INSERT INTO activities.inbox (message_id, consumer, processed_at) VALUES (gen_random_uuid(), 'intruso', now())")]
    [InlineData("INSERT INTO activities.activity_stream (activity_id, time) VALUES (gen_random_uuid(), now())")]
    [InlineData("DELETE FROM activities.activities")]
    [InlineData("CREATE TABLE activities.t11_intruder (id int)")]
    public async Task IngestionRole_CannotReadOrWriteTheActivitiesSchema(string sql)
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();   // lo schema e le tabelle esistono davvero: l'errore e' di permesso, non di oggetto mancante

        var error = await RunAsFailingAsync(db.ConnectionStringFor("ingestion"), sql);

        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task ActivitiesRole_CanUseItsOwnSchema_IncludingTheHypertable()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();
        var id = Guid.NewGuid();

        await using var insert = new NpgsqlCommand(
            "INSERT INTO activities.activity_stream (activity_id, time, heart_rate) VALUES (@id, now(), 140)", connection);
        insert.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());

        await using var count = new NpgsqlCommand("SELECT count(*) FROM activities.activity_stream WHERE activity_id = @id", connection);
        count.Parameters.AddWithValue("id", id);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task HostDbContext_ConnectsAsTheActivitiesRole_WithTheServiceSchemaFirstInTheSearchPath()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ActivitiesDbContext>();

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_user || '|' || current_schema()";

        Assert.Equal("activities_svc|activities", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public void DevelopmentConnectionString_UsesTheActivitiesRoleAndDatabaseOfTheInitScript()
    {
        var settings = AppSettings.Read("appsettings.Development.json");
        var configured = new NpgsqlConnectionStringBuilder(settings.String("ConnectionStrings", "Activities"));
        var fromFixture = new NpgsqlConnectionStringBuilder(db.ConnectionStringFor("activities"));

        Assert.Equal("activities_svc", configured.Username);
        Assert.Equal(fromFixture.Username, configured.Username);
        Assert.Equal(fromFixture.Password, configured.Password); // password di sviluppo dello script di init
        Assert.Equal(fromFixture.Database, configured.Database);
        Assert.Equal("127.0.0.1", configured.Host);
        Assert.Equal(5432, configured.Port);
    }
}
