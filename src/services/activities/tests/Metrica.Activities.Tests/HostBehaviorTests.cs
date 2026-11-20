using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Aggiunge a un host un endpoint protetto di prova (T11 non ha ancora endpoint di dominio: le API sono di T13): in
/// <c>Production</c> deve rispondere 401 e mai 500 (REQ-021, DEC-006).
/// </summary>
internal sealed class ProtectedEndpointStartupFilter : IStartupFilter
{
    public const string Path = "/__t11/protected";
    public const string MePath = "/__t11/me";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet(Path, () => "segreto").RequireAuthorization();
            endpoints.MapGet(MePath, (System.Security.Claims.ClaimsPrincipal user) => user.FindFirst("sub")?.Value).RequireAuthorization();
        });
    };
}

/// <summary>Avvio, ambienti, health, OpenAPI e migrazioni ripetibili dell'host reale.</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class HostBehaviorTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static HttpClient StartWithProtectedEndpoint(ActivitiesHost host) =>
        host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, ProtectedEndpointStartupFilter>()))
            .CreateClient();

    [Fact]
    public async Task Production_ProtectedEndpointWithoutIdentity_Returns401_AndMigrationsAreNotApplied()
    {
        // Schema di servizio presente ma vuoto: se le migrazioni girassero fuori da Development creerebbero le tabelle.
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new ActivitiesHost("Production", database.ServiceConnectionString, nats.Url);
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await database.CountTablesAsync("activities"));
    }

    [Fact]
    public async Task Production_IgnoresADevelopmentUserIdFromConfiguration()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new ActivitiesHost(
            "Production", database.ServiceConnectionString, nats.Url,
            new Dictionary<string, string?> { ["Dev:UserId"] = Guid.NewGuid().ToString() });
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ContentRoot_IsTheAssemblyFolder_SoAppSettingsDoNotDependOnTheWorkingDirectory()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();

        var contentRoot = host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ContentRootPath;

        Assert.Equal(
            Path.GetDirectoryName(typeof(Program).Assembly.Location),
            Path.GetFullPath(contentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.True(File.Exists(Path.Combine(contentRoot, "appsettings.json")), "appsettings.json deve stare nel content root.");
    }

    [Fact]
    public async Task TwoHostsWithDifferentUserIds_AuthenticateDifferentUsers()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await using var hostA = ActivitiesHost.Development(db, nats, userId: first);
        await using var hostB = ActivitiesHost.Development(db, nats, userId: second);
        using var clientA = StartWithProtectedEndpoint(hostA);
        using var clientB = StartWithProtectedEndpoint(hostB);

        var subA = await clientA.GetStringAsync(ProtectedEndpointStartupFilter.MePath);
        var subB = await clientB.GetStringAsync(ProtectedEndpointStartupFilter.MePath);

        Assert.Equal(first.ToString(), subA);
        Assert.Equal(second.ToString(), subB);
    }

    [Fact]
    public async Task Development_ProtectedEndpoint_IsAuthenticatedAsTheConfiguredUser()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_LiveAndReady_Return200_WhenTheDatabaseIsReachable()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        using var client = host.Start();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Health_Ready_Returns503_WithAWrongConnectionString_AndLiveStays200()
    {
        var wrong = new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionStringFor("activities")) { Password = "password_sbagliata" }.ConnectionString;
        await using var host = new ActivitiesHost("Production", wrong, nats.Url);
        using var client = host.Start();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Health_Ready_Returns503_WhenTheDatabaseBecomesUnreachable_AndLiveStays200()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url);
        using var client = host.Start();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        // Le nuove connessioni sono rifiutate dal server (le connessioni non sono in pool nei test).
        await database.ExecuteOnMaintenanceAsync($"ALTER DATABASE {database.Name} ALLOW_CONNECTIONS false");

        await Eventually.AssertAsync(
            async () => (await client.GetAsync("/health/ready")).StatusCode == HttpStatusCode.ServiceUnavailable,
            Patience,
            "/health/ready deve rispondere 503 con il database irraggiungibile");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Health_Ready_StaysReadyWhileNatsIsDown_AndTheHostStartsWithoutNats()
    {
        await nats.StopAsync();
        try
        {
            await using var host = ActivitiesHost.Development(db, nats);
            using var client = host.Start();

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }
        finally
        {
            await nats.StartAsync();
        }
    }

    [Fact]
    public async Task OpenApi_Document_IsServedInDevelopment()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        using var client = host.Start();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("openapi", out _));
        Assert.True(document.RootElement.TryGetProperty("info", out _));
    }

    [Fact]
    public async Task OpenApi_Document_IsNotServedOutsideDevelopment()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new ActivitiesHost("Production", database.ServiceConnectionString, nats.Url);
        using var client = host.Start();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamWriter_IsRegisteredAsScoped()
    {
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();

        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var a1 = first.ServiceProvider.GetRequiredService<StreamWriter>();
        var a2 = first.ServiceProvider.GetRequiredService<StreamWriter>();
        var b = second.ServiceProvider.GetRequiredService<StreamWriter>();

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b);
    }

    [Fact]
    public async Task Migrations_FromAnEmptySchemaWithTheExtension_CreateTheHypertable()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url);
        host.Start();

        Assert.Equal(5, await database.CountTablesAsync("activities"));
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities' AND hypertable_name = 'activity_stream'"));
    }

    [Fact]
    public async Task Migration_Down_RemovesEveryTable_IncludingTheHypertable_AndUpCanRunAgain()
    {
        // Il Down e' codice di rollback: va provato davvero (con dati dentro), poi la migrazione deve poter ripartire.
        await using var database = await TemporaryDatabase.CreateAsync(db);
        var options = new DbContextOptionsBuilder<ActivitiesDbContext>();
        ActivitiesDbContext.Configure(options, database.ServiceConnectionString);
        await using var context = new ActivitiesDbContext(options.Options);
        await context.Database.MigrateAsync();
        await database.ExecuteAsync(
            "INSERT INTO activities.activity_stream (activity_id, time) VALUES ('00000000-0000-4000-8000-0000000000aa', now())");
        Assert.Equal(5, await database.CountTablesAsync("activities"));

        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context.Database)
            .MigrateAsync("0");

        Assert.Equal(1, await database.CountTablesAsync("activities"));   // resta solo la tabella di storia
        Assert.Equal(0, await database.ScalarAsync("SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities'"));

        await context.Database.MigrateAsync();

        Assert.Equal(5, await database.CountTablesAsync("activities"));
        Assert.Equal(1, await database.ScalarAsync("SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities' AND hypertable_name = 'activity_stream'"));
    }

    [Fact]
    public async Task Host_WiresNatsMessaging_StreamsAreCreated()
    {
        // Senza AddNatsMessaging l'host non creerebbe gli stream e il consumatore di T12 non partirebbe: nessun altro test lo nota.
        await using var host = ActivitiesHost.Development(db, nats);
        host.Start();
        using var limit = new CancellationTokenSource(Patience);

        await host.Services.GetRequiredService<Metrica.Messaging.INatsStreamsReadiness>().WhenReadyAsync(limit.Token);

        var js = host.Services.GetRequiredService<NATS.Client.JetStream.INatsJSContext>();
        Assert.Equal(Metrica.Contracts.EventNames.EventsStream, (await js.GetStreamAsync(Metrica.Contracts.EventNames.EventsStream, cancellationToken: limit.Token)).Info.Config.Name);
        Assert.Equal(Metrica.Contracts.EventNames.DlqStream, (await js.GetStreamAsync(Metrica.Contracts.EventNames.DlqStream, cancellationToken: limit.Token)).Info.Config.Name);
    }

    [Fact]
    public async Task TemporaryDatabase_HasTheSamePublicSchemaPrivilegesAsTheInitScript()
    {
        // Fedelta' del database temporaneo allo script di init: USAGE si', CREATE no su "public" (DEC-014).
        await using var database = await TemporaryDatabase.CreateAsync(db);

        Assert.Equal(1, await database.ScalarAsync("SELECT CASE WHEN has_schema_privilege('activities_svc', 'public', 'USAGE') THEN 1 ELSE 0 END"));
        Assert.Equal(0, await database.ScalarAsync("SELECT CASE WHEN has_schema_privilege('activities_svc', 'public', 'CREATE') THEN 1 ELSE 0 END"));
        Assert.Equal(0, await database.ScalarAsync("SELECT CASE WHEN has_schema_privilege('ingestion_svc', 'activities', 'USAGE') THEN 1 ELSE 0 END"));
    }

    [Fact]
    public async Task Migrations_StartingTwiceOnTheSameDatabase_IsIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);

        await using (var first = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url))
        {
            first.Start();
        }

        await using (var second = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url))
        {
            using var client = second.Start();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }

        Assert.Equal(5, await database.CountTablesAsync("activities"));
        Assert.Equal(1, await database.CountMigrationsAsync());
        Assert.Equal(1, await database.ScalarAsync(
            "SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_schema = 'activities'"));
    }

    [Fact]
    public async Task Migrations_TwoHostsStartingConcurrentlyOnAnEmptySchema_BothStart()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var first = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url);
        await using var second = new ActivitiesHost("Development", database.ServiceConnectionString, nats.Url);
        using var gate = new Barrier(2);

        // Barriera: i due Program.Main arrivano alla Migrate quasi insieme (blocco consultivo di EF/Npgsql).
        var clients = await Task.WhenAll(
            Task.Run(() => { gate.SignalAndWait(); return first.Start(); }),
            Task.Run(() => { gate.SignalAndWait(); return second.Start(); }));

        foreach (var client in clients)
        {
            using (client)
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            }
        }

        Assert.Equal(5, await database.CountTablesAsync("activities"));
        Assert.Equal(1, await database.CountMigrationsAsync());
    }
}
