using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Metrica.Gateway.Tests;

/// <summary>
/// Corpo delle richieste: i multipart arrivano integri e in streaming, il limite (Upload:MaxBytes di ingestion + 1 MiB di
/// overhead multipart = 34.603.008 byte) e' fatto rispettare dal gateway con 413, senza che il backend riceva la richiesta intera.
/// </summary>
public sealed class BodyTests
{
    private const long Limit = 34_603_008;
    private const string Upload = "/api/ingestion/uploads";

    private static byte[] AllByteValues(int totalBytes)
    {
        var bytes = new byte[totalBytes];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 256);
        }

        return bytes;
    }

    private static (MultipartFormDataContent Content, string Boundary) Multipart(byte[] file)
    {
        var boundary = "----gateway-test-boundary-" + Guid.NewGuid().ToString("N");
        var content = new MultipartFormDataContent(boundary);
        var filePart = new ByteArrayContent(file);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(filePart, "file", "attivita.fit");
        content.Add(new StringContent("campo è testuale", Encoding.UTF8), "note");
        return (content, boundary);
    }

    [Fact]
    public async Task Multipart_WithAllByteValues_ArrivesIdentical_WithBoundaryAndContentTypeIntact()
    {
        await using var scenario = await Scenario.StartAsync();
        var (content, boundary) = Multipart(AllByteValues(256 * 1000));
        using var _ = content;
        var sent = await content.ReadAsByteArrayAsync();
        var sentContentType = content.Headers.ContentType!.ToString();

        using var response = await scenario.Client.PostAsync(Upload, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.Equal(sent.Length, received.BodyBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sent)), received.BodySha256);
        Assert.Equal(sent, received.Body);
        Assert.Equal(sentContentType, received.Header("Content-Type"));
        Assert.Contains(boundary, received.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal(sent.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), received.Header("Content-Length"));
    }

    [Fact]
    public async Task LargeMultipart_UnderTheLimit_ArrivesIdentical_ByHashAndLength()
    {
        await using var scenario = await Scenario.StartAsync();
        var (content, _) = Multipart(AllByteValues(20 * 1024 * 1024));
        using var _ = content;
        var sent = await content.ReadAsByteArrayAsync();

        using var response = await scenario.Client.PostAsync(Upload, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.Equal(sent.Length, received.BodyBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sent)), received.BodySha256);
    }

    [Fact]
    public async Task BodyExactlyAtTheLimit_WithContentLength_Passes()
    {
        await using var scenario = await Scenario.StartAsync();
        var body = AllByteValues(checked((int)Limit));

        using var response = await scenario.Client.PostAsync(Upload, new ByteArrayContent(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.Equal(Limit, received.BodyBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(body)), received.BodySha256);
    }

    [Fact]
    public async Task ChunkedBodyJustUnderTheLimit_WithoutContentLength_Passes()
    {
        // Per i corpi chunked Kestrel conta anche il framing (dimensione e CRLF di ogni blocco, circa 9 byte ogni 64 KiB):
        // il limite esatto non e' garantito, per questo si lascia un margine di 16 KiB. Il rifiuto con +1 e' provato piu' sotto.
        await using var scenario = await Scenario.StartAsync();
        var bodyBytes = Limit - 16 * 1024;

        var response = await RawChunkedAsync(scenario.Host.Address, Upload, bodyBytes);

        Assert.StartsWith("HTTP/1.1 200", RawHttp.StatusLine(response), StringComparison.Ordinal);
        var received = scenario.AssertReachedOnly(scenario.Ingestion);
        Assert.Equal(bodyBytes, received.BodyBytes);
        Assert.True(received.BodyCompleted);
    }

    [Fact]
    public async Task BodyOneByteOverTheLimit_WithContentLength_Returns413_AndTheBackendIsNeverContacted()
    {
        await using var scenario = await Scenario.StartAsync();
        // Solo le intestazioni con la lunghezza dichiarata: il rifiuto non deve aspettare il corpo.
        var response = await RawHttp.SendAsync(
            scenario.Host.Address,
            $"POST {Upload} HTTP/1.1\r\nHost: gateway\r\nContent-Type: application/octet-stream\r\nContent-Length: {Limit + 1}\r\n");

        Assert.StartsWith("HTTP/1.1 413 ", RawHttp.StatusLine(response), StringComparison.Ordinal);
        scenario.AssertNoBackendReached();
    }

    [Fact]
    public async Task BodyOneByteOverTheLimit_SentInFull_NeverCompletesAtTheBackend()
    {
        await using var scenario = await Scenario.StartAsync();
        var body = new byte[Limit + 1];

        // Il gateway risponde 413 senza leggere il corpo e chiude: il client vede il 413 o la connessione chiusa durante l'invio.
        try
        {
            using var response = await scenario.Client.PostAsync(Upload, new ByteArrayContent(body));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // connessione chiusa dal gateway durante l'invio del corpo
        }

        scenario.AssertNoBackendReached();
    }

    [Fact]
    public async Task BodyOverTheLimit_WithoutContentLength_Returns413_AndTheBackendDoesNotReceiveTheWholeRequest()
    {
        await using var scenario = await Scenario.StartAsync();

        var response = await RawChunkedAsync(scenario.Host.Address, Upload, Limit + 1);

        Assert.StartsWith("HTTP/1.1 413 ", RawHttp.StatusLine(response), StringComparison.Ordinal);
        foreach (var received in scenario.Ingestion.Requests)
        {
            await received.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(received.BodyCompleted, "il backend ha ricevuto il corpo per intero oltre il limite");
            Assert.True(received.BodyBytes <= Limit, $"il backend ha letto {received.BodyBytes} byte, oltre il limite");
        }

        Assert.Empty(scenario.Activities.Requests);
    }

    [Fact]
    public async Task OverTheLimit_WithHttpClientAndNoContentLength_NeverCompletesAtTheBackend()
    {
        await using var scenario = await Scenario.StartAsync();
        using var content = new GeneratedContent(Limit + 1);

        try
        {
            using var response = await scenario.Client.PostAsync(Upload, content);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // connessione chiusa dal gateway durante l'invio del corpo
        }

        foreach (var received in scenario.Ingestion.Requests)
        {
            await received.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(received.BodyCompleted);
            Assert.True(received.BodyBytes <= Limit);
        }
    }

    [Fact]
    public async Task SmallConfiguredLimit_AppliesToBoundaryAndOneOver()
    {
        var settings = new Dictionary<string, string?> { ["Gateway:MaxRequestBodyBytes"] = "1000" };
        await using var scenario = await Scenario.StartAsync(settings);

        using var atLimit = await scenario.Client.PostAsync(Upload, new ByteArrayContent(new byte[1000]));
        var overWithLength = await RawHttp.SendAsync(
            scenario.Host.Address, $"POST {Upload} HTTP/1.1\r\nHost: gateway\r\nContent-Length: 1001\r\n");
        var overChunked = await RawChunkedAsync(scenario.Host.Address, Upload, 1001);

        Assert.Equal(HttpStatusCode.OK, atLimit.StatusCode);
        Assert.StartsWith("HTTP/1.1 413 ", RawHttp.StatusLine(overWithLength), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 413 ", RawHttp.StatusLine(overChunked), StringComparison.Ordinal);
        Assert.All(scenario.Ingestion.Requests.Skip(1), r => Assert.False(r.BodyCompleted));
    }

    [Fact]
    public async Task TheLimitAlsoAppliesToTheActivitiesRoute()
    {
        await using var scenario = await Scenario.StartAsync();

        var response = await RawHttp.SendAsync(
            scenario.Host.Address, $"POST /api/activities HTTP/1.1\r\nHost: gateway\r\nContent-Length: {Limit + 1}\r\n");

        Assert.StartsWith("HTTP/1.1 413 ", RawHttp.StatusLine(response), StringComparison.Ordinal);
        scenario.AssertNoBackendReached();
    }

    [Fact]
    public async Task Response413FromTheGateway_IsAProblemDetailsWithoutInternalDetails()
    {
        await using var scenario = await Scenario.StartAsync();

        var raw = await RawHttp.SendAsync(
            scenario.Host.Address,
            $"POST {Upload} HTTP/1.1\r\nHost: gateway\r\nContent-Length: {Limit + 1}\r\n",
            readUntilClose: true);

        Assert.StartsWith("HTTP/1.1 413", RawHttp.StatusLine(raw), StringComparison.Ordinal);
        Assert.Contains("application/problem+json", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"status\":413", raw, StringComparison.Ordinal);
        // Il corpo non viene letto: la connessione si chiude invece di restare aperta in attesa di byte inutili.
        Assert.Contains("Connection: close", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
    }

    /// <summary>Invia <paramref name="total"/> byte in chunked (nessun Content-Length) a blocchi da 64 KiB.</summary>
    private static Task<string> RawChunkedAsync(string address, string path, long total) => RawHttp.SendAsync(
        address,
        $"POST {path} HTTP/1.1\r\nHost: gateway\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n",
        async stream =>
        {
            var block = new byte[64 * 1024];
            for (var sent = 0L; sent < total;)
            {
                var size = (int)Math.Min(block.Length, total - sent);
                await RawHttp.WriteChunkAsync(stream, block.AsMemory(0, size));
                sent += size;
            }

            await RawHttp.WriteLastChunkAsync(stream);
        });
}

/// <summary>Corpo generato al volo, senza Content-Length (quindi chunked), con hash calcolato mentre si scrive.</summary>
internal sealed class GeneratedContent(long totalBytes, Func<long, Task>? afterBlock = null, int blockSize = 64 * 1024) : HttpContent
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public string? Sha256 { get; private set; }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        var block = new byte[blockSize];
        for (var sent = 0L; sent < totalBytes;)
        {
            var size = (int)Math.Min(block.Length, totalBytes - sent);
            for (var i = 0; i < size; i++)
            {
                block[i] = (byte)((sent + i) % 251);
            }

            _hash.AppendData(block.AsSpan(0, size));
            await stream.WriteAsync(block.AsMemory(0, size));
            await stream.FlushAsync();
            sent += size;
            if (afterBlock is not null)
            {
                await afterBlock(sent);
            }
        }

        Sha256 = Convert.ToHexString(_hash.GetHashAndReset());
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
