using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>Isolamento dei dati tra servizi (REQ-023, AC-008): il ruolo <c>ingestion_svc</c> vede solo il proprio schema.</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class IsolationTests(TimescaleFixture db, NatsFixture nats)
{
    private async Task<NpgsqlConnection> OpenServiceAsync()
    {
        var connection = new NpgsqlConnection(db.ConnectionStringFor("ingestion"));
        await connection.OpenAsync();
        return connection;
    }

    [Theory]
    [InlineData("SELECT count(*) FROM activities.activities")]
    [InlineData("SELECT count(*) FROM activities.inbox")]
    [InlineData("INSERT INTO activities.activities (id) VALUES (gen_random_uuid())")]
    [InlineData("CREATE TABLE activities.t09_intruder (id int)")]
    [InlineData("SELECT count(*) FROM analytics.anything")]
    [InlineData("CREATE TABLE public.t09_intruder (id int)")]
    public async Task ServiceRole_CannotReadOrWriteOtherSchemas(string sql)
    {
        await using var host = IngestionHost.Development(db, nats);
        host.Start();
        await using var connection = await OpenServiceAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal("42501", error.SqlState); // insufficient_privilege: accesso negato, non "tabella inesistente"
    }

    [Fact]
    public async Task HostDbContext_ConnectsAsTheIngestionRole_WithTheServiceSchemaFirstInTheSearchPath()
    {
        await using var host = IngestionHost.Development(db, nats);
        host.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();

        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_user || '|' || current_schema()";

        Assert.Equal("ingestion_svc|ingestion", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public void DevelopmentConnectionString_UsesTheIngestionRoleAndDatabaseOfTheInitScript()
    {
        var settings = AppSettings.Read("appsettings.Development.json");
        var configured = new NpgsqlConnectionStringBuilder(settings.String("ConnectionStrings", "Ingestion"));
        var fromFixture = new NpgsqlConnectionStringBuilder(db.ConnectionStringFor("ingestion"));

        Assert.Equal("ingestion_svc", configured.Username);
        Assert.Equal(fromFixture.Username, configured.Username);
        Assert.Equal(fromFixture.Password, configured.Password); // password di sviluppo dello script di init
        Assert.Equal(fromFixture.Database, configured.Database);
        Assert.Equal("127.0.0.1", configured.Host);
        Assert.Equal(5432, configured.Port);
    }
}
