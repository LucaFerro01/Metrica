using Npgsql;
using Metrica.TestInfrastructure;
using Xunit;

namespace Metrica.TestInfrastructure.Tests;

/// <summary>
/// Test di fumo sui ruoli e permessi dello script REALE <c>01-schemas.sql</c> (REQ-023, AC-008, DEC-014).
/// </summary>
public sealed class TimescaleFixtureTests(TimescaleFixture db) : IClassFixture<TimescaleFixture>
{
    private const string PermissionDenied = "42501";

    private static string UniqueName(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T?)await command.ExecuteScalarAsync();
    }

    private static async Task AssertPermissionDeniedAsync(NpgsqlConnection connection, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.Equal(PermissionDenied, ex.SqlState);
    }

    [Fact]
    public async Task ActivitiesRole_CanCreateHypertableInsertAndListIt()
    {
        var table = UniqueName("t");
        await using var connection = await OpenAsync(db.ConnectionStringFor("activities"));
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE activities.{table}(time timestamptz NOT NULL, v int)");
            // create_hypertable non qualificata: serve il search_path "activities, public" del ruolo.
            await ExecuteAsync(connection, $"SELECT create_hypertable('activities.{table}', 'time')");
            await ExecuteAsync(connection, $"INSERT INTO activities.{table} VALUES (now(), 1)");

            Assert.Equal(1L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM activities.{table}"));
            Assert.Equal(1L, await ScalarAsync<long>(connection,
                $"SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities' AND hypertable_name = '{table}'"));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS activities.{table}");
        }
    }

    [Theory]
    [InlineData("activities", "ingestion")]
    [InlineData("ingestion", "activities")]
    [InlineData("identity", "analytics")]
    [InlineData("analytics", "identity")]
    [InlineData("training_plan", "identity")]
    public async Task ServiceRole_CannotTouchAnotherServiceSchema(string caller, string owner)
    {
        var table = UniqueName("t");
        // La tabella esiste davvero, creata dal ruolo proprietario dello schema.
        await using var ownerConnection = await OpenAsync(db.ConnectionStringFor(owner));
        await using var callerConnection = await OpenAsync(db.ConnectionStringFor(caller));
        try
        {
            await ExecuteAsync(ownerConnection, $"CREATE TABLE {owner}.{table}(id int)");
            await ExecuteAsync(ownerConnection, $"INSERT INTO {owner}.{table} VALUES (1)");

            await AssertPermissionDeniedAsync(callerConnection, $"SELECT * FROM {owner}.{table}");
            await AssertPermissionDeniedAsync(callerConnection, $"INSERT INTO {owner}.{table} VALUES (2)");
            await AssertPermissionDeniedAsync(callerConnection, $"CREATE TABLE {owner}.{UniqueName("x")}(id int)");

            // Il proprietario continua a vedere solo la propria riga.
            Assert.Equal(1L, await ScalarAsync<long>(ownerConnection, $"SELECT count(*) FROM {owner}.{table}"));
        }
        finally
        {
            await ExecuteAsync(ownerConnection, $"DROP TABLE IF EXISTS {owner}.{table}");
        }
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task ServiceRole_CannotCreateObjectsInPublic(string service)
    {
        await using var connection = await OpenAsync(db.ConnectionStringFor(service));

        await AssertPermissionDeniedAsync(connection, $"CREATE TABLE public.{UniqueName("x")}(id int)");
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task ServiceRole_HasUsageButNotCreateOnPublic(string service)
    {
        // DEC-014: il grant su "public" serve solo a trovare le funzioni dell'estensione (create_hypertable).
        await using var connection = await OpenAsync(db.ConnectionStringFor(service));

        Assert.True(await ScalarAsync<bool>(connection, "SELECT has_schema_privilege(current_user, 'public', 'USAGE')"));
        Assert.False(await ScalarAsync<bool>(connection, "SELECT has_schema_privilege(current_user, 'public', 'CREATE')"));
    }

    [Theory]
    [MemberData(nameof(AllServices))]
    public async Task ServiceRole_SearchPathIsOwnSchemaThenPublic(string service)
    {
        await using var connection = await OpenAsync(db.ConnectionStringFor(service));

        var searchPath = await ScalarAsync<string>(connection, "SHOW search_path");

        Assert.NotNull(searchPath);
        Assert.Equal($"{service}, public", searchPath.Replace(" ", "").Replace(",", ", "));
    }

    [Fact]
    public async Task AdminConnection_IsSuperuser()
    {
        await using var connection = await OpenAsync(db.AdminConnectionString);

        Assert.Equal("on", await ScalarAsync<string>(connection, "SHOW is_superuser"));
    }

    [Fact]
    public void ConnectionStringFor_UnknownService_Throws()
    {
        Assert.Throws<ArgumentException>(() => db.ConnectionStringFor("unknown"));
    }

    public static TheoryData<string> AllServices() => new(TimescaleFixture.Services);
}
