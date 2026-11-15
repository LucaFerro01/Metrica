using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace TriMetric.Gateway.Tests;

/// <summary>Le risposte dei backend passano com'e'; backend giu' = 502 senza dettagli interni; salute senza chiamare i backend.</summary>
public sealed class ResponseTests
{
    [Theory]
    [InlineData(400, "/api/ingestion/uploads", true)]
    [InlineData(404, "/api/ingestion/uploads", true)]
    [InlineData(413, "/api/ingestion/uploads", true)]
    [InlineData(422, "/api/ingestion/uploads", true)]
    [InlineData(400, "/api/activities?page=0", false)]
    [InlineData(404, "/api/activities/3f2a9c1e-7b64-4d0a-8e55-0a1b2c3d4e5f", false)]
    [InlineData(422, "/api/activities", false)]
    public async Task BackendProblemDetails_AreForwardedUnchanged(int status, string target, bool fromIngestion)
    {
        var body = $$$"""{"type":"https://tools.ietf.org/html/rfc9110","title":"Errore del backend","status":{{{status}}},"detail":"dettaglio è qui","errors":{"file":["non valido"]}}""";
        Task Respond(HttpContext context, RecordedRequest _)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json; charset=utf-8";
            context.Response.Headers["X-Backend-Header"] = "dal-backend";
            return context.Response.WriteAsync(body, Encoding.UTF8);
        }

        await using var scenario = await Scenario.StartAsync(
            ingestionRespond: fromIngestion ? Respond : null,
            activitiesRespond: fromIngestion ? null : Respond);

        using var response = await scenario.Client.SendAsync(new HttpRequestMessage(
            fromIngestion ? HttpMethod.Post : HttpMethod.Get, target) { Content = fromIngestion ? new StringContent("x") : null });

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("dal-backend", response.Headers.GetValues("X-Backend-Header").Single());
    }

    [Fact]
    public async Task BackendBinaryResponse_KeepsContentHeadersAndBytes()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await using var scenario = await Scenario.StartAsync(activitiesRespond: (context, _) =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"a.bin\"";
            return context.Response.Body.WriteAsync(bytes).AsTask();
        });

        using var response = await scenario.Client.GetAsync("/api/activities");

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/api/ingestion/uploads")]
    [InlineData("/api/activities")]
    public async Task BackendDown_Returns502_WithoutInternalDetails(string target)
    {
        // Nessun backend in ascolto: porta chiusa su loopback.
        await using var host = new GatewayHost();
        using var client = host.StartClient();

        using var response = await client.GetAsync(target);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Yarp", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneBackendDown_DoesNotAffectTheOther()
    {
        await using var activities = await StubBackend.StartAsync();
        await using var host = new GatewayHost(activitiesAddress: activities.Address); // ingestion non in ascolto
        using var client = host.StartClient();

        using var down = await client.GetAsync("/api/ingestion/uploads");
        using var up = await client.GetAsync("/api/activities");

        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
    }

    [Fact]
    public async Task HealthLive_Returns200_WithoutCallingTheBackends()
    {
        await using var scenario = await Scenario.StartAsync();

        using var live = await scenario.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        scenario.AssertNoBackendReached();
    }

    [Fact]
    public async Task HealthLive_Returns200_EvenWhenBothBackendsAreDown()
    {
        await using var host = new GatewayHost();
        using var client = host.StartClient();

        using var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task HealthReady_HasNoChecks_SoItIsHealthyWithoutCallingTheBackends()
    {
        // Il gateway non ha database: /health/ready (di ServiceDefaults) non ha controlli registrati e risponde 200 finche'
        // il processo e' vivo. Non sonda i backend: la loro salute e' dei loro /health/ready.
        await using var scenario = await Scenario.StartAsync();

        using var ready = await scenario.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        scenario.AssertNoBackendReached();
    }
}
