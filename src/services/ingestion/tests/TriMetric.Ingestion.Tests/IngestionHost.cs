using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// L'host reale di <c>TriMetric.Ingestion</c> (<c>Program</c>) in processo, con stringa di connessione e URL di NATS dei
/// contenitori di prova. La configurazione si sovrascrive con valori in memoria letti alla prima risoluzione dei servizi.
/// </summary>
internal sealed class IngestionHost(
    string environment,
    string? connectionString,
    string natsUrl,
    IReadOnlyDictionary<string, string?>? extra = null,
    Guid? userId = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        if (userId is { } id)
        {
            // ATTENZIONE: Dev:UserId e' letto da AddDevelopmentIdentity alla COSTRUZIONE dell'host (builder.Configuration).
            // ConfigureAppConfiguration qui sotto e' applicato dopo: per questa chiave e' IGNORATO IN SILENZIO e resta
            // l'UUID di appsettings.Development.json. UseSetting invece e' visibile gia' alla costruzione e funziona.
            builder.UseSetting("Dev:UserId", id.ToString());
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Nats:Url"] = natsUrl,
                ["Outbox:PollIntervalMs"] = "50",
                ["Outbox:PublishTimeoutSeconds"] = "5",
            };
            if (connectionString is not null)
            {
                settings["ConnectionStrings:Ingestion"] = connectionString;
            }

            foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }

    /// <summary>Host in <c>Development</c> sul database condiviso come ruolo <c>ingestion_svc</c> (le migrazioni si applicano all'avvio).</summary>
    public static IngestionHost Development(TimescaleFixture db, NatsFixture nats, IReadOnlyDictionary<string, string?>? extra = null, Guid? userId = null) =>
        new("Development", db.ConnectionStringFor("ingestion"), nats.Url, extra, userId);

    /// <summary>Avvia l'host (i servizi si risolvono alla prima richiesta o a <c>Services</c>) e restituisce un client.</summary>
    public HttpClient Start()
    {
        _ = Services;
        return CreateClient();
    }
}

/// <summary>
/// Database vuoto dedicato al test, con il solo schema <c>ingestion</c> posseduto da <c>ingestion_svc</c> (come lo script
/// di init): permette di verificare avvii su uno schema pulito senza toccare il database condiviso.
/// </summary>
internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly TimescaleFixture _fixture;

    private TemporaryDatabase(TimescaleFixture fixture, string name)
    {
        _fixture = fixture;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Connessione del superutente al database temporaneo.</summary>
    public string AdminConnectionString => WithDatabase(_fixture.AdminConnectionString);

    /// <summary>Connessione come <c>ingestion_svc</c> al database temporaneo.</summary>
    public string ServiceConnectionString => WithDatabase(_fixture.ConnectionStringFor("ingestion"));

    public static async Task<TemporaryDatabase> CreateAsync(TimescaleFixture fixture, bool createServiceSchema = true)
    {
        var database = new TemporaryDatabase(fixture, $"t09_{Guid.NewGuid():N}");
        await database.ExecuteOnMaintenanceAsync($"CREATE DATABASE {database.Name}");
        if (createServiceSchema)
        {
            await using var connection = new NpgsqlConnection(database.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE SCHEMA ingestion AUTHORIZATION ingestion_svc", connection);
            await command.ExecuteNonQueryAsync();
        }

        return database;
    }

    public Task ExecuteOnMaintenanceAsync(string sql) => RunAsync(_fixture.AdminConnectionString, sql);

    public async Task<long> CountTablesAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<long> CountMigrationsAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ingestion.\"__EFMigrationsHistory\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() =>
        await RunAsync(_fixture.AdminConnectionString, $"DROP DATABASE IF EXISTS {Name} WITH (FORCE)");

    private string WithDatabase(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = Name }.ConnectionString;

    private static async Task RunAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Attese con polling e timeout (mai attese fisse lunghe).</summary>
internal static class Eventually
{
    public static async Task AssertAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(await condition(), $"Condizione non raggiunta entro {timeout.TotalSeconds:0.#} s: {description}");
    }
}

/// <summary>Posizione dei file del servizio nel repository (i test leggono i file di configurazione reali).</summary>
internal static class ServicePaths
{
    public static string ProjectDirectory { get; } = Path.Combine(
        RepositoryRoot.Path, "src", "services", "ingestion", "src", "TriMetric.Ingestion");

    public static IReadOnlyList<string> AppSettingsFiles() =>
        Directory.GetFiles(ProjectDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);
}
