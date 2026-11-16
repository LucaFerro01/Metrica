using System.Net;
using System.Text;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>AC-004 (REQ-009, REQ-035): file non validi rifiutati con 4xx e <c>ProblemDetails</c>, senza attivita', eventi ne' righe.</summary>
[Collection(E2eCollection.Name)]
public sealed class UploadValidationTests(TimescaleFixture db, NatsFixture nats)
{
    private const long IngestionMaxBytes = 33_554_432;       // Upload:MaxBytes predefinito: 32 MiB
    private const long GatewayMaxBodyBytes = 34_603_008;     // Gateway:MaxRequestBodyBytes: 32 MiB + 1 MiB

    public static TheoryData<string> InvalidCases =>
        new() { "empty-gpx", "text-file", "malformed-gpx", "binary-fit", "gpx-without-points" };

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task Ac004_InvalidFile_IsRejectedWithAClearProblem_AndNothingIsCreated(string kind)
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var seed = Guid.NewGuid();
        var track = new GpxTrack { Seed = seed, Points = 20, Start = new DateTimeOffset(2024, 3, 1, 7, 0, 0, TimeSpan.Zero) };
        var valid = Encoding.UTF8.GetString(track.Build().Content);

        var (name, content, expectedStatus, expectedType) = kind switch
        {
            "empty-gpx" => ($"vuoto-{seed:N}.gpx", Array.Empty<byte>(), HttpStatusCode.BadRequest, "empty-file"),
            "text-file" => ($"appunti-{seed:N}.txt", Encoding.UTF8.GetBytes($"niente da importare {seed:N}"), HttpStatusCode.UnsupportedMediaType, "unsupported-file-type"),
            // GPX troncato a meta' (con coordinate vere dentro): XML non ben formato.
            "malformed-gpx" => ($"rotto-{seed:N}.gpx", Encoding.UTF8.GetBytes(valid[..(valid.Length / 2)]), HttpStatusCode.UnprocessableEntity, "invalid-activity-file"),
            "binary-fit" => ($"falso-{seed:N}.fit", Enumerable.Range(0, 2048).Select(i => (byte)(i * 31 + seed.ToByteArray()[i % 16])).ToArray(), HttpStatusCode.UnprocessableEntity, "invalid-activity-file"),
            // XML ben formato ma senza alcun punto.
            "gpx-without-points" => ($"senzapunti-{seed:N}.gpx", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg/></trk></gpx>"), HttpStatusCode.UnprocessableEntity, "invalid-activity-file"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        system.Protect(track.Probes(name));

        var response = await system.UploadAsync(new GeneratedFile(name, content, new PrivacyProbes().AddText("nome del file", name)));

        Expect.Status(expectedStatus, response);
        AssertClearProblem(response, expectedType, name);
        await AssertNothingCreatedAsync(system);
        system.AssertLogsContainNoPersonalData();
    }

    [Fact]
    public async Task Ac004_FileJustOverTheLimit_ThroughTheGateway_IsRejected_AndNothingIsCreated()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var seed = Guid.NewGuid();

        // Poco oltre i 32 MiB e sotto il limite del gateway (34.603.008): il rifiuto viene da ingestion.
        var content = OversizedContent(IngestionMaxBytes + 64 * 1024, seed);
        Assert.True(content.Length + 1024 < GatewayMaxBodyBytes);
        var name = $"troppo-grande-{seed:N}.gpx";
        system.Protect(new PrivacyProbes().AddText("nome del file", name));

        // Attraverso il gateway l'esito non e' deterministico (T14, I1): un 413 leggibile OPPURE la connessione interrotta mentre
        // il client scrive. Per questo qui si accettano entrambi (la misura sulla frequenza e' nel test dedicato) e il controllo
        // vero e' l'assenza di qualsiasi traccia.
        var (response, failure) = await system.GatewayClient.TryUploadAsync(GatewayApi.UploadPath, name, content);

        AssertRejectedAsTooLargeOrInterrupted(response, failure, name);
        // Il gateway ha inoltrato la richiesta (e' sotto il suo limite): a rifiutare e' ingestion.
        Assert.Contains(system.Ingestion.Requests.All, r => r.Method == "POST" && r.PathAndQuery.StartsWith("/uploads", StringComparison.Ordinal));
        await AssertNothingCreatedAsync(system);
        await AssertGatewayStillWorksAsync(system);
        system.AssertLogsContainNoPersonalData();
    }

    [Fact]
    public async Task Ac004_FileJustOverTheLimit_DirectlyOnIngestion_Returns413_Deterministically()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var seed = Guid.NewGuid();
        var content = OversizedContent(IngestionMaxBytes + 64 * 1024, seed);
        var name = $"troppo-grande-{seed:N}.gpx";
        system.Protect(new PrivacyProbes().AddText("nome del file", name));

        // Con Expect: 100-continue il server decide dall'intestazione e risponde prima che il client invii il corpo: nessuna
        // connessione interrotta a meta' invio, quindi il 413 e' sempre leggibile.
        var response = await system.IngestionClient.UploadAsync("/uploads", name, content, expectContinue: true);

        Expect.Status(HttpStatusCode.RequestEntityTooLarge, response);
        AssertClearProblem(response, "file-too-large", name);
        Assert.Contains(IngestionMaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), response.ProblemDetail);
        await AssertNothingCreatedAsync(system);
        system.AssertLogsContainNoPersonalData();
    }

    [Fact]
    public async Task Ac004_FileOverTheGatewayLimit_IsRejected_BackendNeverReached_AndNothingIsCreated()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var seed = Guid.NewGuid();
        var content = OversizedContent(GatewayMaxBodyBytes + 64 * 1024, seed);
        var name = $"enorme-{seed:N}.gpx";
        system.Protect(new PrivacyProbes().AddText("nome del file", name));

        var (response, failure) = await system.GatewayClient.TryUploadAsync(GatewayApi.UploadPath, name, content);

        AssertRejectedAsTooLargeOrInterrupted(response, failure, name);
        await AssertNothingCreatedAsync(system);
        AssertBackendNotReached(system);

        // Con Expect: 100-continue il rifiuto del gateway e' deterministico e leggibile (il corpo non viene nemmeno inviato).
        var polite = await system.GatewayClient.UploadAsync(GatewayApi.UploadPath, name, content, expectContinue: true);
        Expect.Status(HttpStatusCode.RequestEntityTooLarge, polite);
        Assert.Equal("application/problem+json", polite.MediaType);
        await AssertNothingCreatedAsync(system);
        AssertBackendNotReached(system);
        system.AssertLogsContainNoPersonalData();

        // Controllo positivo per ultimo (crea un'attivita'): dopo le interruzioni il gateway lavora ancora.
        await AssertGatewayStillWorksAsync(system);
    }

    /// <summary>Ingestion non ha ricevuto alcun upload: il rifiuto e' del gateway, prima di contattare il backend.</summary>
    private static void AssertBackendNotReached(EndToEndSystem system) =>
        Assert.DoesNotContain(system.Ingestion.Requests.All, r => r.Method == "POST");

    private static void AssertRejectedAsTooLargeOrInterrupted(ApiResponse? response, HttpRequestException? failure, string fileName)
    {
        if (response is not null)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
            AssertClearProblem(response, "file-too-large", fileName);
        }
        else
        {
            // Il rifiuto e' arrivato come connessione interrotta durante l'invio: un errore di I/O o di socket, non un altro
            // HttpRequestException (ad esempio un indirizzo errato o un rifiuto di connessione).
            Assert.NotNull(failure);
            Assert.True(
                IsConnectionInterruption(failure!),
                $"attesa una connessione interrotta (IOException/SocketException), ottenuto: {failure}");
        }
    }

    private static bool IsConnectionInterruption(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.IO.IOException or System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Controllo positivo dopo un rifiuto o un'interruzione: il gateway (e i servizi dietro) funzionano ancora, quindi il rifiuto non
    /// e' stato un guasto. L'elenco risponde 200 e un upload valido, dallo stesso gateway, e' accettato (202).
    /// </summary>
    private static async Task AssertGatewayStillWorksAsync(EndToEndSystem system)
    {
        Expect.Status(HttpStatusCode.OK, await system.Api.ListAsync());
        var valid = new GpxTrack { Seed = Guid.NewGuid(), Points = 30, Start = new DateTimeOffset(2024, 3, 2, 7, 0, 0, TimeSpan.Zero) }.Build();
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(valid));
    }

    /// <summary>
    /// <c>ProblemDetails</c> (RFC 7807) con <c>type</c> <c>urn:trimetric:problem:...</c>, titolo e messaggio non vuoti e nessun
    /// riferimento al nome del file.
    /// </summary>
    private static void AssertClearProblem(ApiResponse response, string expectedCode, string fileName)
    {
        Assert.Equal("application/problem+json", response.MediaType);
        Assert.Equal($"urn:trimetric:problem:{expectedCode}", response.ProblemType);
        Assert.Equal((int)response.Status, response.Document.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(response.Document.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(response.ProblemDetail));
        Assert.DoesNotContain(Path.GetFileNameWithoutExtension(fileName), response.Body, StringComparison.OrdinalIgnoreCase);
        Assert.InRange((int)response.Status, 400, 499);
    }

    /// <summary>Nessuna attivita', nessun evento (nel server NATS), nessuna riga di upload o di outbox, nessun record in activities.</summary>
    private static async Task AssertNothingCreatedAsync(EndToEndSystem system)
    {
        Assert.Equal(0, (await system.Api.ListPageAsync()).Total);
        Assert.Equal(0, await system.NatsProbe.EventsCountAsync());
        Assert.Equal(0L, await system.Db.UploadCountAsync());
        Assert.Empty(await system.Db.OutboxAsync());
        Assert.Equal(0L, await system.Db.ActivityCountAsync());
        Assert.Equal(0, await system.NatsProbe.DlqCountAsync());
    }

    /// <summary>Contenuto di <paramref name="size"/> byte con un'intestazione GPX valida e il resto a riempimento (basta la dimensione).</summary>
    private static byte[] OversizedContent(long size, Guid seed)
    {
        var content = new byte[size];
        var header = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?><gpx version=\"1.1\"><!-- {seed:N} -->");
        header.CopyTo(content, 0);
        Array.Fill(content, (byte)' ', header.Length, content.Length - header.Length);
        return content;
    }
}
