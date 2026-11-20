using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Metrica.TestInfrastructure;

namespace Metrica.Ingestion.Tests;

/// <summary>
/// Aggiunge a un host un endpoint protetto di prova (T09 non ha ancora endpoint di dominio): in <c>Production</c>
/// deve rispondere 401 e mai 500 (REQ-021, DEC-006).
/// </summary>
internal sealed class ProtectedEndpointStartupFilter : IStartupFilter
{
    public const string Path = "/__t09/protected";
    public const string MePath = "/__t09/me";

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

    private static HttpClient StartWithProtectedEndpoint(IngestionHost host) =>
        host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddTransient<IStartupFilter, ProtectedEndpointStartupFilter>()))
            .CreateClient();

    [Fact]
    public async Task Production_ProtectedEndpointWithoutIdentity_Returns401_AndMigrationsAreNotApplied()
    {
        // Schema di servizio presente ma vuoto: se le migrazioni girassero fuori da Development creerebbero le tabelle.
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new IngestionHost("Production", database.ServiceConnectionString, nats.Url);
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await database.CountTablesAsync("ingestion"));
    }

    [Fact]
    public async Task Production_IgnoresADevelopmentUserIdFromConfiguration()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new IngestionHost(
            "Production", database.ServiceConnectionString, nats.Url,
            new Dictionary<string, string?> { ["Dev:UserId"] = Guid.NewGuid().ToString() });
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ContentRoot_IsTheAssemblyFolder_SoAppSettingsDoNotDependOnTheWorkingDirectory()
    {
        await using var host = IngestionHost.Development(db, nats);
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
        await using var hostA = IngestionHost.Development(db, nats, userId: first);
        await using var hostB = IngestionHost.Development(db, nats, userId: second);
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
        await using var host = IngestionHost.Development(db, nats);
        using var client = StartWithProtectedEndpoint(host);

        var response = await client.GetAsync(ProtectedEndpointStartupFilter.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_LiveAndReady_Return200_WhenTheDatabaseIsReachable()
    {
        await using var host = IngestionHost.Development(db, nats);
        using var client = host.Start();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Health_Ready_Returns503_WhenTheDatabaseBecomesUnreachable_AndLiveStays200()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new IngestionHost("Development", database.ServiceConnectionString, nats.Url);
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
            await using var host = IngestionHost.Development(db, nats);
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
        await using var host = IngestionHost.Development(db, nats);
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
        await using var host = new IngestionHost("Production", database.ServiceConnectionString, nats.Url);
        using var client = host.Start();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Migrations_StartingTwiceOnTheSameDatabase_IsIdempotent()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);

        await using (var first = new IngestionHost("Development", database.ServiceConnectionString, nats.Url))
        {
            first.Start();
        }

        await using (var second = new IngestionHost("Development", database.ServiceConnectionString, nats.Url))
        {
            using var client = second.Start();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        }

        Assert.Equal(3, await database.CountTablesAsync("ingestion"));
        Assert.Equal(1, await database.CountMigrationsAsync());
    }

    [Fact]
    public async Task Migrations_TwoHostsStartingConcurrentlyOnAnEmptySchema_BothStart()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var first = new IngestionHost("Development", database.ServiceConnectionString, nats.Url);
        await using var second = new IngestionHost("Development", database.ServiceConnectionString, nats.Url);
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

        Assert.Equal(3, await database.CountTablesAsync("ingestion"));
        Assert.Equal(1, await database.CountMigrationsAsync());
    }
}
