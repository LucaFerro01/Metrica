using System.Text;

namespace Metrica.Gateway.Tests;

/// <summary>
/// REQ-034: nei log del gateway non compaiono valori di <c>Authorization</c>, delle intestazioni di identita' rimosse, della
/// query string, nomi di file o contenuto del corpo (nemmeno nei log di YARP, che a Information registrerebbe l'URL con la query).
/// </summary>
public sealed class PrivacyTests
{
    private const string Marker = "SEGRETO";

    private static HttpRequestMessage SensitiveRequest(string path)
    {
        var boundary = "confine-" + Marker;
        var body = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{Marker}-nome-file.fit\"\r\n" +
                   $"Content-Type: application/octet-stream\r\n\r\n{Marker}-contenuto-del-corpo\r\n--{boundary}--\r\n";
        var request = new HttpRequestMessage(HttpMethod.Post, $"{path}?userId={Marker}-userid&note={Marker}-parametro&sport=run")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data") { Parameters = { new("boundary", boundary) } };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Marker}-token");
        request.Headers.TryAddWithoutValidation("X-User-Id", $"{Marker}-utente");
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", $"{Marker}-forwarded");
        request.Headers.TryAddWithoutValidation("X-Auth-Token", $"{Marker}-auth");
        return request;
    }

    private static void AssertNothingSensitive(InMemoryLoggerProvider logs)
    {
        var offending = logs.Entries.Where(e => e.Contains(Marker, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(offending.Count == 0, "log con dati sensibili: " + string.Join(" | ", offending));
    }

    [Fact]
    public async Task SuccessfulAndRejectedRequests_LeaveNoSensitiveValueInTheLogs()
    {
        var logs = new InMemoryLoggerProvider();
        await using var scenario = await Scenario.StartAsync(logs: logs);

        using var uploaded = await scenario.Client.SendAsync(SensitiveRequest("/api/ingestion/uploads"));
        using var listed = await scenario.Client.SendAsync(SensitiveRequest("/api/activities"));
        using var unknown = await scenario.Client.SendAsync(SensitiveRequest("/api/unknown"));
        var tooBig = await RawHttp.SendAsync(
            scenario.Host.Address,
            $"POST /api/ingestion/uploads?note={Marker}-parametro HTTP/1.1\r\nHost: gateway\r\nAuthorization: Bearer {Marker}-token\r\nContent-Length: 99999999\r\n");

        Assert.StartsWith("HTTP/1.1 413", RawHttp.StatusLine(tooBig), StringComparison.Ordinal);
        AssertNothingSensitive(logs);
    }

    [Fact]
    public async Task BackendDown_LogsTheFailure_ButNoSensitiveValue()
    {
        var logs = new InMemoryLoggerProvider();
        await using var host = new GatewayHost(logs: logs); // nessun backend in ascolto
        using var client = host.StartClient();

        using var response = await client.SendAsync(SensitiveRequest("/api/ingestion/uploads"));

        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
        // Il provider di prova funziona: il guasto del backend e' registrato (a livello Warning o superiore).
        Assert.NotEmpty(logs.Entries);
        AssertNothingSensitive(logs);
    }

    [Fact]
    public async Task ProxyInformationLogs_AreSilenced_SoTheDestinationUrlWithTheQueryNeverAppears()
    {
        var logs = new InMemoryLoggerProvider();
        await using var scenario = await Scenario.StartAsync(logs: logs);

        using var response = await scenario.Client.GetAsync("/api/activities?note=" + Marker + "&sport=run");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(logs.Entries, e => e.Contains("Yarp.ReverseProxy.Forwarder", StringComparison.Ordinal) && e.Contains("[Information]", StringComparison.Ordinal));
        AssertNothingSensitive(logs);
    }
}
