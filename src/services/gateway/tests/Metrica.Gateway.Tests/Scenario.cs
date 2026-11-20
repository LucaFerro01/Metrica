using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Metrica.Gateway.Tests;

/// <summary>Gateway reale davanti a due server di prova (ingestion e activities) su porte effimere.</summary>
internal sealed class Scenario : IAsyncDisposable
{
    private Scenario(StubBackend ingestion, StubBackend activities, GatewayHost host, HttpClient client)
    {
        Ingestion = ingestion;
        Activities = activities;
        Host = host;
        Client = client;
    }

    public StubBackend Ingestion { get; }

    public StubBackend Activities { get; }

    public GatewayHost Host { get; }

    public HttpClient Client { get; }

    public static async Task<Scenario> StartAsync(
        IReadOnlyDictionary<string, string?>? settings = null,
        ILoggerProvider? logs = null,
        Func<HttpContext, RecordedRequest, Task>? ingestionRespond = null,
        Func<HttpContext, RecordedRequest, Task>? activitiesRespond = null)
    {
        var ingestion = await StubBackend.StartAsync(ingestionRespond);
        var activities = await StubBackend.StartAsync(activitiesRespond);
        var host = new GatewayHost(ingestion.Address, activities.Address, settings, logs);
        return new Scenario(ingestion, activities, host, host.StartClient());
    }

    /// <summary>Il backend atteso ha ricevuto una sola richiesta e l'altro nessuna.</summary>
    public RecordedRequest AssertReachedOnly(StubBackend expected)
    {
        var other = ReferenceEquals(expected, Ingestion) ? Activities : Ingestion;
        Assert.Empty(other.Requests);
        return Assert.Single(expected.Requests);
    }

    public void AssertNoBackendReached()
    {
        Assert.Empty(Ingestion.Requests);
        Assert.Empty(Activities.Requests);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.DisposeAsync();
        await Ingestion.DisposeAsync();
        await Activities.DisposeAsync();
    }
}
