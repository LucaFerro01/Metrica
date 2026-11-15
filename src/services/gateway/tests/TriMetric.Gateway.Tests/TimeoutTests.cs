using System.Net;
using System.Diagnostics;

namespace TriMetric.Gateway.Tests;

/// <summary>
/// Timeout di YARP: <c>ActivityTimeout</c> e' un timeout di INATTIVITA' (si riazzera a ogni operazione sul corpo o sulla risposta),
/// non un tempo massimo della richiesta: un upload lento ma che avanza non viene tagliato, uno fermo si'. Il valore di produzione
/// (100 s) e' nel file di configurazione; qui lo si porta a 1 s per provare il comportamento.
/// </summary>
public sealed class TimeoutTests
{
    private static Dictionary<string, string?> ShortIdleTimeout() => new()
    {
        ["ReverseProxy:Clusters:ingestion:HttpRequest:ActivityTimeout"] = "00:00:01",
        ["ReverseProxy:Clusters:activities:HttpRequest:ActivityTimeout"] = "00:00:01",
    };

    [Fact]
    public async Task SlowButProgressingUpload_LastingLongerThanTheIdleTimeout_IsNotCut()
    {
        await using var scenario = await Scenario.StartAsync(ShortIdleTimeout());
        // 8 pezzi da 1 KiB a distanza di 400 ms: oltre 3 s in tutto, mai fermo per piu' di 1 s.
        using var content = new GeneratedContent(8 * 1024, _ => Task.Delay(400), blockSize: 1024);
        var stopwatch = Stopwatch.StartNew();

        using var response = await scenario.Client.PostAsync("/api/ingestion/uploads", content);

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stopwatch.Elapsed > TimeSpan.FromSeconds(2.5), "l'invio doveva durare piu' dell'ActivityTimeout");
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.True(received.BodyCompleted);
        Assert.Equal(8 * 1024, received.BodyBytes);
    }

    [Fact]
    public async Task StalledBackend_IsCutByTheIdleTimeout_With504()
    {
        await using var scenario = await Scenario.StartAsync(
            ShortIdleTimeout(),
            activitiesRespond: async (context, _) => await Task.Delay(TimeSpan.FromSeconds(8), context.RequestAborted));

        using var response = await scenario.Client.GetAsync("/api/activities");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }
}
