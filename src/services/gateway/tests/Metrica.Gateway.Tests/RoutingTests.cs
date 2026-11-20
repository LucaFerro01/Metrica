using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Metrica.Gateway.Tests;

/// <summary>Instradamento e riscrittura dei percorsi (REQ-020): ogni richiesta arriva al backend giusto e solo a quello.</summary>
public sealed class RoutingTests
{
    private const string Id = "3f2a9c1e-7b64-4d0a-8e55-0a1b2c3d4e5f";

    [Fact]
    public async Task PostIngestionUploads_ReachesIngestionAsPostUploads_AndNotActivities()
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.PostAsync("/api/ingestion/uploads", new StringContent("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/uploads", request.RawTarget);
    }

    [Theory]
    [InlineData("/api/ingestion/uploads", "/uploads")]
    [InlineData("/api/ingestion/uploads/", "/uploads/")]
    [InlineData("/api/ingestion/uploads?x=1", "/uploads?x=1")]
    [InlineData("/api/ingestion/uploads?a=%20b&c=d+e&c=f&g=%2F&h=", "/uploads?a=%20b&c=d+e&c=f&g=%2F&h=")]
    [InlineData("/api/ingestion/uploads/a%20b", "/uploads/a%20b")]
    public async Task IngestionPaths_AreRewrittenWithoutThePrefix_AndTheQueryIsUntouched(string clientTarget, string backendTarget)
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync(clientTarget);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(backendTarget, scenario.AssertReachedOnly(scenario.Ingestion).RawTarget);
    }

    [Theory]
    [InlineData("/api/activities", "/activities")]
    [InlineData("/api/activities/", "/activities/")]
    [InlineData("/api/activities/" + Id, "/activities/" + Id)]
    [InlineData("/api/activities/" + Id + "/", "/activities/" + Id + "/")]
    [InlineData("/api/activities?sport=run&page=2&pageSize=50", "/activities?sport=run&page=2&pageSize=50")]
    [InlineData("/api/activities?sport=run%20x&q=a+b&q=c&e=&f=%2B%2F", "/activities?sport=run%20x&q=a+b&q=c&e=&f=%2B%2F")]
    [InlineData("/api/activities/" + Id + "?maxPoints=500", "/activities/" + Id + "?maxPoints=500")]
    [InlineData("/api/activities/a%20b", "/activities/a%20b")]
    public async Task ActivitiesPaths_ReachActivitiesUnderTheActivitiesPrefix_NeverDoubleSlashes(string clientTarget, string backendTarget)
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync(clientTarget);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = scenario.AssertReachedOnly(scenario.Activities);
        Assert.Equal(backendTarget, request.RawTarget);
        Assert.DoesNotContain("//", request.Path);
    }

    [Theory]
    [InlineData("/api/activitiesXYZ")]
    [InlineData("/api/activitiesXYZ/abc")]
    [InlineData("/api/activities2")]
    [InlineData("/api/ingestionXYZ")]
    [InlineData("/api/ingestionXYZ/uploads")]
    [InlineData("/api/unknown")]
    [InlineData("/api/unknown/x")]
    [InlineData("/api/training-plan/plans")]
    [InlineData("/api/analytics/x")]
    [InlineData("/api")]
    [InlineData("/api/")]
    [InlineData("/")]
    [InlineData("/uploads")]
    [InlineData("/activities")]
    [InlineData("/activities/" + Id)]
    [InlineData("/x/api/activities")]
    public async Task UnexpectedPaths_Return404FromTheGateway_WithoutReachingAnyBackend(string target)
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync(target);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        scenario.AssertNoBackendReached();
    }

    [Theory]
    [InlineData("GET", "/api/ingestion/uploads", true)]
    [InlineData("POST", "/api/ingestion/uploads", true)]
    [InlineData("DELETE", "/api/ingestion/uploads", true)]
    [InlineData("PUT", "/api/ingestion/uploads", true)]
    [InlineData("PATCH", "/api/ingestion/uploads", true)]
    [InlineData("GET", "/api/activities", false)]
    [InlineData("POST", "/api/activities", false)]
    [InlineData("DELETE", "/api/activities/" + Id, false)]
    [InlineData("PUT", "/api/activities/" + Id, false)]
    [InlineData("PATCH", "/api/activities/" + Id, false)]
    public async Task Methods_AreForwardedUnchanged(string method, string target, bool toIngestion)
    {
        await using var scenario = await Scenario.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), target);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent("payload");
        }

        using var response = await scenario.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reached = scenario.AssertReachedOnly(toIngestion ? scenario.Ingestion : scenario.Activities);
        Assert.Equal(method, reached.Method);
    }

    [Fact]
    public async Task Head_IsForwardedAsHead()
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/activities"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("HEAD", scenario.AssertReachedOnly(scenario.Activities).Method);
    }

    [Fact]
    public async Task BareIngestionPrefix_IsDocumentedAs_ForwardedToTheBackendRoot()
    {
        // /api/ingestion/{**remainder} accetta anche il prefisso senza resto: ingestion riceve "/" (suo 404). Comportamento
        // dichiarato nel README; il test lo fissa cosi' una modifica non passa inosservata.
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync("/api/ingestion");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", scenario.AssertReachedOnly(scenario.Ingestion).RawTarget);
    }

    [Fact]
    public async Task StandInBackendRouting_AcceptsTheTrailingSlashSentByTheGateway()
    {
        // Il gateway inoltra "/activities/" per "/api/activities/": il vero activities (minimal API con MapGet("/activities"))
        // deve rispondere 200 anche con la barra finale, altrimenti la trasformazione scelta sarebbe sbagliata.
        await using var app = StubBackend.CreateLoopbackBuilder().Build();
        app.MapGet("/activities", () => "list");
        app.MapGet("/activities/{id:guid}", (Guid id) => id.ToString());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        await using var host = new GatewayHost(activitiesAddress: address);
        using var client = host.StartClient();

        using var list = await client.GetAsync("/api/activities/");
        using var detail = await client.GetAsync("/api/activities/" + Id + "/");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("list", await list.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(Id, await detail.Content.ReadAsStringAsync());
    }
}
