using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TriMetric.TestInfrastructure;

namespace TriMetric.Activities.Tests;

/// <summary>
/// L'host reale di <c>TriMetric.Activities</c> (<c>Program</c>) in processo, con stringa di connessione e URL di NATS dei
/// contenitori di prova. La configurazione si sovrascrive con valori in memoria letti alla prima risoluzione dei servizi.
/// </summary>
/// <remarks>
/// <para><b>Trappola della configurazione.</b> <c>extra</c> passa da <c>ConfigureAppConfiguration</c>, applicato DOPO la
/// costruzione dell'host: vale solo per i valori letti alla prima risoluzione dei servizi (<c>ConnectionStrings:Activities</c>,
/// <c>Nats:Url</c>). Una chiave letta da <c>Program</c> mentre registra i servizi (<c>Dev:UserId</c>, le opzioni del
/// consumatore <c>Consumers:ActivityImported:*</c>) con <c>extra</c> e' IGNORATA IN SILENZIO: va in <c>startupSettings</c>
/// (<c>UseSetting</c>, visibile gia' alla costruzione).</para>
/// <para>Ogni host avviato ha il consumatore durevole <c>activities-activity-imported</c> sul NATS condiviso: l'isolamento
/// tra test e' descritto in <see cref="ImportScenario"/>.</para>
/// </remarks>
internal sealed class ActivitiesHost(
    string environment,
    string? connectionString,
    string natsUrl,
    IReadOnlyDictionary<string, string?>? extra = null,
    Guid? userId = null,
    IReadOnlyDictionary<string, string?>? startupSettings = null,
    Action<IServiceCollection>? configureServices = null,
    ILoggerProvider? logs = null) : WebApplicationFactory<Program>
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

        foreach (var (key, value) in startupSettings ?? new Dictionary<string, string?>())
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?> { ["Nats:Url"] = natsUrl };
            if (connectionString is not null)
            {
                settings["ConnectionStrings:Activities"] = connectionString;
            }

            foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });
    }

    /// <summary>Host in <c>Development</c> sul database condiviso come ruolo <c>activities_svc</c> (le migrazioni si applicano all'avvio).</summary>
    public static ActivitiesHost Development(
        TimescaleFixture db,
        NatsFixture nats,
        IReadOnlyDictionary<string, string?>? extra = null,
        Guid? userId = null,
        IReadOnlyDictionary<string, string?>? startupSettings = null,
        Action<IServiceCollection>? configureServices = null,
        ILoggerProvider? logs = null) =>
        new("Development", db.ConnectionStringFor("activities"), nats.Url, extra, userId, startupSettings, configureServices, logs);

    /// <summary>Avvia l'host (i servizi si risolvono alla prima richiesta o a <c>Services</c>) e restituisce un client.</summary>
    public HttpClient Start()
    {
        _ = Services;
        return CreateClient();
    }
}

/// <summary>
/// Database vuoto dedicato al test, preparato come lo script di init (<c>deploy/postgres/init/01-schemas.sql</c>) prepara
/// <c>trimetric</c> per <c>activities_svc</c>: estensione <c>timescaledb</c>, <c>REVOKE ALL</c> su <c>public</c> a PUBLIC,
/// <c>GRANT USAGE</c> su <c>public</c> al ruolo e schema <c>activities</c> posseduto dal ruolo (DEC-014). Permette di verificare
/// avvii su uno schema pulito senza toccare il database condiviso. Il ruolo di servizio e' quello vero, mai il superutente.
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

    /// <summary>Connessione come <c>activities_svc</c> al database temporaneo.</summary>
    public string ServiceConnectionString => WithDatabase(_fixture.ConnectionStringFor("activities"));

    public static async Task<TemporaryDatabase> CreateAsync(TimescaleFixture fixture)
    {
        var database = new TemporaryDatabase(fixture, $"t11_{Guid.NewGuid():N}");
        await RunAsync(fixture.AdminConnectionString, $"CREATE DATABASE {database.Name}");

        // Come 01-schemas.sql: l'estensione risiede in "public", il ruolo ne ha USAGE ma non CREATE.
        await RunAsync(
            database.AdminConnectionString,
            """
            CREATE EXTENSION IF NOT EXISTS timescaledb;
            CREATE SCHEMA activities AUTHORIZATION activities_svc;
            GRANT USAGE ON SCHEMA public TO activities_svc;
            REVOKE ALL ON SCHEMA public FROM PUBLIC;
            """);
        return database;
    }

    public Task ExecuteOnMaintenanceAsync(string sql) => RunAsync(_fixture.AdminConnectionString, sql);

    public Task ExecuteAsync(string sql) => RunAsync(AdminConnectionString, sql);

    public Task<long> CountTablesAsync(string schema) => ScalarAsync(
        "SELECT count(*) FROM information_schema.tables WHERE table_schema = '" + schema + "'");

    public Task<long> CountMigrationsAsync() => ScalarAsync("SELECT count(*) FROM activities.\"__EFMigrationsHistory\"");

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

/// <summary>
/// Applica la migrazione sul database condiviso (come ruolo <c>activities_svc</c>) una sola volta per processo di test,
/// per i test che non hanno bisogno di un host. Fuori dai test la migrazione si applica solo all'avvio dell'host in Development.
/// </summary>
internal static class MigratedDatabase
{
    private static readonly ConcurrentDictionary<string, Lazy<Task>> Applied = new();

    public static Task EnsureAsync(TimescaleFixture db) =>
        Applied.GetOrAdd(db.AdminConnectionString, _ => new Lazy<Task>(async () =>
        {
            await using var context = NewContext(db);
            await context.Database.MigrateAsync();
        })).Value;

    /// <summary>Contesto dedicato al test come <c>activities_svc</c>; la connessione si apre solo se serve.</summary>
    public static ActivitiesDbContext NewContext(TimescaleFixture db)
    {
        var options = new DbContextOptionsBuilder<ActivitiesDbContext>();
        ActivitiesDbContext.Configure(options, db.ConnectionStringFor("activities"));
        return new ActivitiesDbContext(options.Options);
    }
}

/// <summary>Attese con polling e timeout (mai attese fisse lunghe).</summary>
internal static class Eventually
{
    public static Task AssertAsync(Func<bool> condition, TimeSpan timeout, string description) =>
        AssertAsync(() => Task.FromResult(condition()), timeout, description);

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
        RepositoryRoot.Path, "src", "services", "activities", "src", "TriMetric.Activities");

    public static IReadOnlyList<string> AppSettingsFiles() =>
        Directory.GetFiles(ProjectDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);
}
