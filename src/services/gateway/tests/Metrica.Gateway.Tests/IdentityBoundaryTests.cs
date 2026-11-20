using System.Net;

namespace Metrica.Gateway.Tests;

/// <summary>
/// Confine di sicurezza (REQ-021): le intestazioni e il parametro <c>userId</c> con cui il client dichiara un'identita' non
/// raggiungono i backend, che ricavano l'utente solo dal proprio principal. <c>Authorization</c> e il correlationId passano.
/// </summary>
public sealed class IdentityBoundaryTests
{
    private const string Id = "3f2a9c1e-7b64-4d0a-8e55-0a1b2c3d4e5f";
    private const string Secret = "identita-del-client-7731";

    // Copia indipendente della regola (non e' il codice di produzione): nomi esatti e prefissi, senza distinzione di maiuscole.
    private static readonly string[] ForbiddenPrefixes = ["X-User", "X-Auth", "X-Identity", "X-Claim", "X-Principal", "X-Authenticated"];

    private static readonly string[] ForbiddenNames =
    [
        "X-User-Id", "X-UserId", "X-User", "X-Forwarded-User", "X-Remote-User", "X-Authenticated-User", "X-Auth-User",
        "X-Identity", "X-Principal", "X-Claims",
    ];

    private static bool IsIdentityHeader(string name) =>
        ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static TheoryData<string, string> RoutesAndHeaders()
    {
        var headers = new[]
        {
            "X-User-Id", "x-user-id", "X-USER-ID", "X-UserId", "x-userid", "X-User", "X-Forwarded-User", "x-forwarded-user",
            "X-Remote-User", "X-Authenticated-User", "X-Auth-User", "X-Identity", "X-Principal", "X-Claims",
            "X-User-Email", "X-Username", "X-Auth-Token", "X-AUTH-Roles", "X-Auth", "X-Claim-Sub", "X-Claim", "X-Claims-Role",
            "X-Identity-Provider", "X-Principal-Name", "X-Authenticated-Userid", "X-Authenticated",
        };
        var data = new TheoryData<string, string>();
        foreach (var route in new[] { "/api/ingestion/uploads", "/api/activities", "/api/activities/" + Id })
        {
            foreach (var header in headers)
            {
                data.Add(route, header);
            }
        }

        return data;
    }

    private static RecordedRequest BackendRequest(Scenario scenario, string route) =>
        route.StartsWith("/api/ingestion", StringComparison.Ordinal)
            ? scenario.AssertReachedOnly(scenario.Ingestion)
            : scenario.AssertReachedOnly(scenario.Activities);

    [Theory]
    [MemberData(nameof(RoutesAndHeaders))]
    public async Task IdentityHeaders_NeverReachTheBackend(string route, string header)
    {
        await using var scenario = await Scenario.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation(header, Secret);

        using var response = await scenario.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = BackendRequest(scenario, route);
        Assert.DoesNotContain(received.HeaderNames, IsIdentityHeader);
        Assert.DoesNotContain(received.Headers, h => h.Value.Contains(Secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("X-Forwarded-Email")]
    [InlineData("X-Forwarded-Preferred-Username")]
    [InlineData("X-Forwarded-Groups")]
    [InlineData("Remote-User")]
    public async Task OtherCommonProxyIdentityHeaders_AreRemovedToo(string header)
    {
        await using var scenario = await Scenario.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/activities");
        request.Headers.TryAddWithoutValidation(header, Secret);

        using var response = await scenario.Client.SendAsync(request);

        Assert.False(scenario.AssertReachedOnly(scenario.Activities).HasHeader(header));
    }

    [Theory]
    [InlineData("/api/ingestion/uploads")]
    [InlineData("/api/activities")]
    public async Task AllIdentityHeadersTogether_AreRemoved_WhileTheOthersPass(string route)
    {
        await using var scenario = await Scenario.StartAsync();
        var correlationId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent("corpo", System.Text.Encoding.UTF8, "text/plain") };
        foreach (var name in RoutesAndHeaders().Select(row => (string)row[1]).Distinct(StringComparer.Ordinal))
        {
            request.Headers.TryAddWithoutValidation(name, Secret);
        }

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer eyJ.token.firma");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        request.Headers.TryAddWithoutValidation("X-Custom-Header", "resta");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "fetch");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await scenario.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = BackendRequest(scenario, route);
        Assert.DoesNotContain(received.HeaderNames, IsIdentityHeader);
        Assert.Equal("Bearer eyJ.token.firma", received.Header("Authorization"));
        Assert.Equal(correlationId, received.Header("X-Correlation-Id"));
        Assert.Equal("resta", received.Header("X-Custom-Header"));
        Assert.Equal("fetch", received.Header("X-Requested-With"));
        Assert.StartsWith("text/plain", received.Header("Content-Type"));
        Assert.Equal("corpo", System.Text.Encoding.UTF8.GetString(received.Body!));
        Assert.True(received.HasHeader("Content-Length"));
    }

    [Theory]
    [InlineData("/api/ingestion/uploads")]
    [InlineData("/api/activities")]
    public async Task DuplicateIdentityHeaders_AreAllRemoved(string route)
    {
        await using var scenario = await Scenario.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("X-User-Id", [Secret + "-1", Secret + "-2"]);
        request.Headers.TryAddWithoutValidation("x-user-id", Secret + "-3");
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", [Secret + "-4", Secret + "-5"]);

        using var response = await scenario.Client.SendAsync(request);

        var received = BackendRequest(scenario, route);
        Assert.DoesNotContain(received.HeaderNames, IsIdentityHeader);
        Assert.DoesNotContain(received.Headers, h => h.Value.Contains(Secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/ingestion/uploads")]
    [InlineData("/api/activities")]
    public async Task TheGatewayNeverAddsIdentity_WhenTheClientSendsNone(string route)
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync(route);

        var received = BackendRequest(scenario, route);
        Assert.DoesNotContain(received.HeaderNames, IsIdentityHeader);
        Assert.False(received.HasHeader("Authorization"));
    }

    [Fact]
    public async Task ForwardedHeadersSetByTheProxy_AreNotRemoved()
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.GetAsync("/api/activities");

        var received = scenario.AssertReachedOnly(scenario.Activities);
        Assert.True(received.HasHeader("X-Forwarded-For"));
        Assert.True(received.HasHeader("X-Forwarded-Proto"));
        Assert.True(received.HasHeader("X-Forwarded-Host"));
    }

    [Fact]
    public async Task ForwardedHeadersFromTheClient_AreReplacedByTheOnesOfTheProxy()
    {
        await using var scenario = await Scenario.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/activities");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "6.6.6.6");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");

        using var response = await scenario.Client.SendAsync(request);

        var received = scenario.AssertReachedOnly(scenario.Activities);
        Assert.Equal("127.0.0.1", received.Header("X-Forwarded-For"));
        Assert.Equal("http", received.Header("X-Forwarded-Proto"));
        Assert.DoesNotContain("evil.example", received.Header("X-Forwarded-Host"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("userId=abc", "")]
    [InlineData("userId=", "")]
    [InlineData("userId", "")]
    [InlineData("sport=run&page=2&userId=abc", "?sport=run&page=2")]
    [InlineData("userId=abc&sport=run&page=2", "?sport=run&page=2")]
    [InlineData("sport=run&userId=abc&page=2", "?sport=run&page=2")]
    [InlineData("a=1&userid=x&b=2&USERID=y&UserId=z&c=3", "?a=1&b=2&c=3")]
    [InlineData("userId=a&userId=b", "")]
    [InlineData("&userId=1", "")]
    [InlineData("userId=1&", "")]
    [InlineData("&userId=1&sport=run", "?&sport=run")]
    [InlineData("userId=a&sport=run&userId=b", "?sport=run")]
    [InlineData("q=a%20b+c&userId=x&r=%2Fz&s=1&s=2", "?q=a%20b+c&r=%2Fz&s=1&s=2")]
    [InlineData("userIdX=1&xuserId=2&user_id=3&userId2=4&user=5&id=6", "?userIdX=1&xuserId=2&user_id=3&userId2=4&user=5&id=6")]
    [InlineData("sport=run&page=2", "?sport=run&page=2")]
    [InlineData("note=userId%3Dabc&k=v", "?note=userId%3Dabc&k=v")]
    public async Task UserIdQueryParameter_IsRemoved_OtherParametersAndTheirEncodingStayIntact(string clientQuery, string backendQuery)
    {
        foreach (var (clientPath, backendPath, toIngestion) in new[]
        {
            ("/api/activities", "/activities", false),
            ("/api/ingestion/uploads", "/uploads", true),
        })
        {
            await using var scenario = await Scenario.StartAsync();

            using var response = await scenario.Client.GetAsync($"{clientPath}?{clientQuery}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var received = scenario.AssertReachedOnly(toIngestion ? scenario.Ingestion : scenario.Activities);
            Assert.Equal(backendPath + backendQuery, received.RawTarget);
        }
    }

    [Theory]
    [InlineData("user%49d=abc&k=v", "?k=v")]
    [InlineData("%75serid=abc&k=v&%55SERID=x", "?k=v")]
    [InlineData("k=v&user%49d", "?k=v")]
    public async Task UserIdQueryParameter_WithAnEncodedName_IsRemoved(string clientQuery, string backendQuery)
    {
        // Con socket grezzo: HttpClient normalizzerebbe %49 in "I" prima dell'invio e il caso non verrebbe provato.
        await using var scenario = await Scenario.StartAsync();

        var response = await RawHttp.SendAsync(
            scenario.Host.Address, $"GET /api/activities?{clientQuery} HTTP/1.1\r\nHost: gateway\r\nConnection: close\r\n");

        Assert.StartsWith("HTTP/1.1 200", RawHttp.StatusLine(response), StringComparison.Ordinal);
        Assert.Equal("/activities" + backendQuery, scenario.AssertReachedOnly(scenario.Activities).RawTarget);
    }

    [Fact]
    public async Task UserIdQueryParameter_IsRemovedOnPostToo()
    {
        await using var scenario = await Scenario.StartAsync();

        using var response = await scenario.Client.PostAsync("/api/ingestion/uploads?userId=abc&source=web", new StringContent("x"));

        Assert.Equal("/uploads?source=web", scenario.AssertReachedOnly(scenario.Ingestion).RawTarget);
    }

    [Fact]
    public async Task IdentityHeadersAndUserId_AreRemovedEvenOnRoutesAddedOnlyByConfigurationOrTransformedByIt()
    {
        // La rimozione e' di codice e vale per ogni rotta, anche per una aggiunta dal file di configurazione e anche se una
        // trasformazione di configurazione prova a impostare l'identita': il gateway non aggiunge mai identita'.
        var settings = new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:extra:ClusterId"] = "ingestion",
            ["ReverseProxy:Routes:extra:Match:Path"] = "/api/extra/{**remainder}",
            ["ReverseProxy:Routes:extra:Transforms:0:PathRemovePrefix"] = "/api/extra",
            ["ReverseProxy:Routes:ingestion:Transforms:1:RequestHeader"] = "X-User-Id",
            ["ReverseProxy:Routes:ingestion:Transforms:1:Set"] = "iniettata-dalla-configurazione",
        };
        await using var scenario = await Scenario.StartAsync(settings);
        using var extra = new HttpRequestMessage(HttpMethod.Get, "/api/extra/uploads?userId=abc&k=v");
        extra.Headers.TryAddWithoutValidation("X-User-Id", Secret);
        using var viaConfiguredHeader = new HttpRequestMessage(HttpMethod.Get, "/api/ingestion/uploads");

        using var first = await scenario.Client.SendAsync(extra);
        using var second = await scenario.Client.SendAsync(viaConfiguredHeader);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, scenario.Ingestion.Requests.Count);
        Assert.All(scenario.Ingestion.Requests, r => Assert.DoesNotContain(r.HeaderNames, IsIdentityHeader));
        Assert.Equal("/uploads?k=v", scenario.Ingestion.Requests[0].RawTarget);
    }

    [Fact]
    public async Task CorrelationId_IsPropagatedToTheBackend_AndMatchesTheResponseHeader()
    {
        await using var scenario = await Scenario.StartAsync();
        var supplied = Guid.NewGuid().ToString();
        using var withId = new HttpRequestMessage(HttpMethod.Get, "/api/activities");
        withId.Headers.TryAddWithoutValidation("X-Correlation-Id", supplied);
        using var withoutId = new HttpRequestMessage(HttpMethod.Get, "/api/activities");
        using var withInvalidId = new HttpRequestMessage(HttpMethod.Get, "/api/activities");
        withInvalidId.Headers.TryAddWithoutValidation("X-Correlation-Id", "non-un-guid");

        using var first = await scenario.Client.SendAsync(withId);
        using var second = await scenario.Client.SendAsync(withoutId);
        using var third = await scenario.Client.SendAsync(withInvalidId);

        var requests = scenario.Activities.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal(supplied, requests[0].Header("X-Correlation-Id"));
        Assert.Equal(supplied, first.Headers.GetValues("X-Correlation-Id").Single());
        // Senza identificativo valido ne genera uno il gateway: lo stesso arriva al backend e torna al client.
        Assert.Equal(second.Headers.GetValues("X-Correlation-Id").Single(), requests[1].Header("X-Correlation-Id"));
        Assert.Equal(third.Headers.GetValues("X-Correlation-Id").Single(), requests[2].Header("X-Correlation-Id"));
        Assert.True(Guid.TryParse(requests[2].Header("X-Correlation-Id"), out _));
    }
}
