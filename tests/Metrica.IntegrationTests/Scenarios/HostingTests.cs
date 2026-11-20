using System.Net;
using Metrica.IntegrationTests.Support;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Scenarios;

/// <summary>
/// Avvio dei tre servizi reali: porte effimere (mai quelle di sviluppo), configurazione reale di ciascuno, gateway collegato
/// agli indirizzi reali degli altri due.
/// </summary>
[Collection(E2eCollection.Name)]
public sealed class HostingTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly int[] ForbiddenPorts = [5080, 5081, 5082, 5432, 4222];

    [Fact]
    public async Task ThreeRealServices_ListenOnDistinctEphemeralPorts_AndTheGatewayReachesBoth()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);

        var addresses = new[] { system.Ingestion.Address, system.Activities.Address, system.Gateway.Address };
        var ports = addresses.Select(a => new Uri(a).Port).ToArray();
        Assert.Equal(3, ports.Distinct().Count());
        Assert.All(addresses, a => Assert.StartsWith("http://127.0.0.1:", a));
        Assert.All(ports, port =>
        {
            Assert.InRange(port, 1, 65535);
            Assert.DoesNotContain(port, ForbiddenPorts);
        });

        // Il NATS e il database sono quelli delle fixture, non lo stack di sviluppo.
        Assert.DoesNotContain(ForbiddenPorts, p => nats.Url.EndsWith($":{p}", StringComparison.Ordinal));
        Assert.DoesNotContain(ForbiddenPorts, p => new Npgsql.NpgsqlConnectionStringBuilder(db.ConnectionStringFor("ingestion")).Port == p);

        // Attraverso il gateway si raggiungono davvero i due servizi (e non altro): salute dei backend e rotta sconosciuta.
        Expect.Status(HttpStatusCode.OK, await system.GatewayClient.GetAsync("/api/ingestion/health/ready"));
        Expect.Status(HttpStatusCode.OK, await system.GatewayClient.GetAsync("/api/activities"));
        Expect.Status(HttpStatusCode.NotFound, await system.GatewayClient.GetAsync("/api/altro"));

        // Ognuno dei due servizi e' stato avviato con l'identita' di sviluppo dello scenario: l'elenco (vuoto) e' dell'utente giusto.
        Assert.Equal(0, (await system.Api.ListPageAsync()).Total);
    }

    [Fact]
    public void ServiceConfiguration_LoadsTheRealAppSettingsOfEachService()
    {
        var ingestion = ServiceConfiguration.Load("ingestion", "Metrica.Ingestion");
        var activities = ServiceConfiguration.Load("activities", "Metrica.Activities");
        var gateway = ServiceConfiguration.Load("gateway", "Metrica.Gateway");

        // Valori che stanno nei file reali (non nei test): se i file cambiano, questo test lo dice.
        Assert.Equal("http://127.0.0.1:5081", ingestion["Kestrel:Endpoints:Http:Url"]);
        Assert.Equal("33554432", ingestion["Upload:MaxBytes"]);
        Assert.StartsWith("Host=127.0.0.1;Port=5432;Database=metrica;Username=ingestion_svc", ingestion["ConnectionStrings:Ingestion"]);
        Assert.Equal("http://127.0.0.1:5082", activities["Kestrel:Endpoints:Http:Url"]);
        Assert.StartsWith("Host=127.0.0.1;Port=5432;Database=metrica;Username=activities_svc", activities["ConnectionStrings:Activities"]);
        Assert.Equal("34603008", gateway["Gateway:MaxRequestBodyBytes"]);
        Assert.Equal("/api/ingestion/{**remainder}", gateway["ReverseProxy:Routes:ingestion:Match:Path"]);
        Assert.Equal("/api", gateway["ReverseProxy:Routes:activities:Transforms:0:PathRemovePrefix"]);
        Assert.DoesNotContain("Upload:MaxBytes", gateway.Keys);   // niente configurazione di un servizio nell'altro
        Assert.DoesNotContain("ReverseProxy:Routes:ingestion:ClusterId", ingestion.Keys);
    }

    /// <summary>
    /// I file reali dei tre servizi sono coerenti tra loro: se uno cambia (utente di sviluppo, porta, indirizzo del bus) e gli altri
    /// no, lo stack di sviluppo non funziona. I test non usano questi valori (li sovrascrivono): li si controlla qui.
    /// </summary>
    [Fact]
    public void RealAppSettings_AreAlignedAcrossServices()
    {
        var ingestion = ServiceConfiguration.Load("ingestion", "Metrica.Ingestion");
        var activities = ServiceConfiguration.Load("activities", "Metrica.Activities");
        var gateway = ServiceConfiguration.Load("gateway", "Metrica.Gateway");

        // Stessa identita' di sviluppo, e un UUID valido (non vuoto).
        Assert.Equal(ingestion["Dev:UserId"], activities["Dev:UserId"]);
        Assert.True(Guid.TryParseExact(ingestion["Dev:UserId"], "D", out var userId), "Dev:UserId non e' un UUID");
        Assert.NotEqual(Guid.Empty, userId);

        // Stesso bus.
        Assert.Equal(ingestion["Nats:Url"], activities["Nats:Url"]);

        // Il gateway punta, con la sola destinazione "primary" di ogni cluster, all'indirizzo su cui ogni servizio ascolta.
        ServiceConfiguration.AssertSinglePrimaryDestination(gateway, "ingestion");
        ServiceConfiguration.AssertSinglePrimaryDestination(gateway, "activities");
        Assert.Equal(
            ingestion["Kestrel:Endpoints:Http:Url"].TrimEnd('/'),
            gateway["ReverseProxy:Clusters:ingestion:Destinations:primary:Address"].TrimEnd('/'));
        Assert.Equal(
            activities["Kestrel:Endpoints:Http:Url"].TrimEnd('/'),
            gateway["ReverseProxy:Clusters:activities:Destinations:primary:Address"].TrimEnd('/'));
    }
}
