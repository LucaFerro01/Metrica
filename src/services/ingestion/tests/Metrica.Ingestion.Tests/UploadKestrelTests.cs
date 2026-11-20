using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Metrica.TestInfrastructure;

namespace Metrica.Ingestion.Tests;

/// <summary>
/// Con Kestrel vero (il server di prova non applica i limiti del corpo): il tetto del corpo e' rispettato anche dal server,
/// con e senza <c>Content-Length</c>, e il servizio resta in vita.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadKestrelTests(TimescaleFixture db, NatsFixture nats)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealKestrel_OversizedBody_Returns413_AndTheServiceStaysAlive(bool chunked)
    {
        var userId = Guid.NewGuid();
        await using var host = IngestionHost.Development(
            db, nats,
            new Dictionary<string, string?> { ["Upload:MaxBytes"] = "2048", ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0" },
            userId);
        host.UseKestrel();
        host.StartServer();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };

        var big = new byte[4 * 1024 * 1024];
        var raw = UploadRequests.RawMultipart(
            "limite",
            ("Content-Disposition: form-data; name=\"file\"; filename=\"enorme.gpx\"\r\nContent-Type: application/octet-stream", big));
        using var content = UploadRequests.RawContent("limite", raw, chunked);
        // Il server risponde 413 e chiude senza leggere il resto del corpo: a seconda dei tempi il client fa in tempo a leggere
        // la risposta oppure vede la connessione chiusa mentre ancora scrive. Sono due esiti validi dello stesso rifiuto.
        try
        {
            using var response = await client.PostAsync("/uploads", content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // connessione chiusa dal server durante l'invio del corpo
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await UploadDb.AssertNothingWrittenAsync(db, userId);
    }

    [Fact]
    public async Task RealKestrel_ValidUpload_Works()
    {
        var userId = Guid.NewGuid();
        await using var host = IngestionHost.Development(
            db, nats, new Dictionary<string, string?> { ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0" }, userId);
        host.UseKestrel();
        host.StartServer();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var form = UploadRequests.Form("a.gpx", UploadSamples.Run());

        using var response = await client.PostAsync("/uploads", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await UploadDb.OutboxAsync(db, userId));
    }
}
