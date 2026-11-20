using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Metrica.Ingestion.Parsing;
using Metrica.TestInfrastructure;

namespace Metrica.Ingestion.Tests;

/// <summary>Nessun dato del file nei log (REQ-034) e documento OpenAPI dell'endpoint (DEC-007).</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadPrivacyAndApiTests(TimescaleFixture db, NatsFixture nats)
{
    [Fact]
    public async Task Logs_NeverContainCoordinatesHeartRateFileContentOrTheFileName()
    {
        var logs = new LogCapture();
        var secret = "SEGRETO" + Guid.NewGuid().ToString("N");
        var fileName = $"nome-{Guid.NewGuid():N}.gpx";
        await using var app = UploadApp.Start(
            db, nats,
            new Dictionary<string, string?> { ["Upload:MaxBytes"] = "20000" },
            configureLogging: l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var coordinates = new[] { "45.1234567", "7.7654321" };
        var heartRates = new[] { "187", "193", "199" };
        string Points(int count) => string.Concat(Enumerable.Range(0, count).Select(i => GpxBuilder.Pt(
            coordinates[0], coordinates[1], GpxBuilder.T(i), ele: "123.4", inner: GpxBuilder.Tpx(heartRates[i % 3], "81"))));
        string Gpx(int count, string? tail = null) => GpxBuilder.Doc($"<trk><name>{secret}</name><type>running</type><trkseg>{Points(count)}</trkseg>{tail}</trk>");

        // Valido, duplicato, malformato, senza tempo, estensione errata, troppo grande, vuoto.
        var valid = Encoding.UTF8.GetBytes(Gpx(5));
        await app.UploadAsync(fileName, valid);
        await app.UploadAsync(fileName, valid);
        await app.UploadAsync(fileName, Encoding.UTF8.GetBytes(Gpx(5, "<oops")));
        await app.UploadAsync(fileName, Encoding.UTF8.GetBytes(Gpx(5).Replace("<time>", "<tempo>").Replace("</time>", "</tempo>")));
        await app.UploadAsync(fileName.Replace(".gpx", ".txt"), valid);
        await app.UploadAsync(fileName, Encoding.UTF8.GetBytes(Gpx(500)));
        await app.UploadAsync(fileName, []);
        await app.UploadAsync(fileName, Encoding.UTF8.GetBytes(secret));
        using (var multi = new MultipartFormDataContent())
        {
            UploadRequests.AddFile(multi, "file", fileName, valid);
            UploadRequests.AddFile(multi, "file", fileName, valid);
            await app.PostAsync(multi);
        }

        // Controllo positivo: la cattura funziona e il servizio ha davvero scritto dei log (altrimenti il test non dimostra nulla).
        Assert.Contains(logs.Entries, e => e.Category.StartsWith("Metrica.Ingestion.Upload", StringComparison.Ordinal) && e.Text.Contains("accettato"));
        var all = string.Join(' ', logs.AllText);
        foreach (var forbidden in new[] { secret, fileName, fileName.Replace(".gpx", ""), coordinates[0], coordinates[1] })
        {
            Assert.DoesNotContain(forbidden, all);
        }

        // Valori brevi (FC, quota): si cercano solo nei log dell'endpoint, dove non ci sono numeri casuali (quelli di EF
        // e del relay contengono durate, contatori e GUID le cui cifre possono coincidere: flake osservato una volta su 7 esecuzioni).
        // (gli id dei caricamenti sono GUID: le loro cifre esadecimali possono contenere "187" ecc., quindi si tolgono prima)
        var own = System.Text.RegularExpressions.Regex.Replace(
            string.Join(' ', logs.Entries.Where(e => e.Category == "Metrica.Ingestion.Upload").Select(e => e.Text)),
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<guid>");
        foreach (var forbidden in heartRates.Concat(["123.4"]))
        {
            Assert.DoesNotMatch($@"(?<![0-9.]){System.Text.RegularExpressions.Regex.Escape(forbidden)}(?![0-9])", own);
        }
    }

    [Fact]
    public async Task UnexpectedParserFailure_Returns500Generic_AndLogsOnlyTheExceptionType()
    {
        var logs = new LogCapture();
        var secret = "SEGRETO" + Guid.NewGuid().ToString("N");
        await using var app = UploadApp.Start(
            db, nats, null,
            ParserOverrides.Replace(new ExplodingParser(secret)),
            l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));

        var reply = await app.UploadAsync($"{secret}.gpx", UploadSamples.Run());

        Assert.Equal(HttpStatusCode.InternalServerError, reply.Status);
        Assert.Equal("application/problem+json", reply.MediaType);
        Assert.DoesNotContain(secret, reply.Body);
        Assert.DoesNotContain("InvalidOperationException", reply.Body);
        Assert.DoesNotContain("   at ", reply.Body);
        var all = string.Join(' ', logs.AllText);
        Assert.DoesNotContain(secret, all);
        Assert.DoesNotContain("   at ", all);   // niente stack trace
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Text.Contains(nameof(InvalidOperationException)));
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesTheUploadEndpoint()
    {
        await using var app = UploadApp.Start(db, nats);

        var text = await app.Client.GetStringAsync("/openapi/v1.json");

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var post = root.GetProperty("paths").GetProperty("/uploads").GetProperty("post");
        Assert.Equal("UploadActivityFile", post.GetProperty("operationId").GetString());

        var responses = post.GetProperty("responses");
        foreach (var status in new[] { "200", "202", "400", "413", "415", "422" })
        {
            Assert.True(responses.TryGetProperty(status, out _), $"manca la risposta {status}");
        }

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var response = schemas.GetProperty("UploadResponse");
        foreach (var property in new[] { "uploadId", "externalId", "status" })
        {
            Assert.True(response.GetProperty("properties").TryGetProperty(property, out _), $"UploadResponse senza {property}");
        }

        Assert.True(schemas.TryGetProperty("ProblemDetails", out _));
        Assert.Contains("UploadResponse", responses.GetProperty("202").GetRawText());
        Assert.Contains("UploadResponse", responses.GetProperty("200").GetRawText());
        Assert.Contains("ProblemDetails", responses.GetProperty("422").GetRawText());
        Assert.Contains("application/problem+json", responses.GetProperty("415").GetRawText());

        var multipart = post.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        var form = Resolve(root, multipart);
        var file = Resolve(root, form.GetProperty("properties").GetProperty("file"));
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        while (schema.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString()!.TrimStart('#', '/').Split('/');
            schema = path.Aggregate(root, (element, segment) => element.GetProperty(segment));
        }

        return schema;
    }

    private sealed class ExplodingParser(string secret) : IActivityFileParser
    {
        public bool CanParse(string fileName) => fileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase);

        public ParsedActivity Parse(Stream content, CancellationToken ct) =>
            throw new InvalidOperationException($"guasto interno con {secret}");
    }
}
