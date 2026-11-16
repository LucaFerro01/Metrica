using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TriMetric.IntegrationTests.Files;

namespace TriMetric.IntegrationTests.Support;

// DTO delle risposte dichiarati qui (non quelli dei servizi): il test legge il contratto HTTP come lo leggerebbe il web.
internal sealed record ListItem(Guid Id, string ExternalId, string Sport, DateTimeOffset StartTime, double DistanceMeters, double ElapsedSeconds, double? ElevationGainMeters);

internal sealed record ListPage(IReadOnlyList<ListItem> Items, int Page, int PageSize, int Total);

internal sealed record SummaryDto(double? AvgHeartRate, double? MaxHeartRate, double? AvgPowerWatts, double? AvgCadence);

internal sealed record LapDto(DateTimeOffset StartTime, double ElapsedSeconds, double DistanceMeters);

internal sealed record StreamDto(
    IReadOnlyList<double> TimeOffsetSeconds,
    IReadOnlyList<double?>? Latitude,
    IReadOnlyList<double?>? Longitude,
    IReadOnlyList<double?>? AltitudeMeters,
    IReadOnlyList<double?>? HeartRate,
    IReadOnlyList<double?>? PowerWatts,
    IReadOnlyList<double?>? Cadence,
    IReadOnlyList<double?>? SpeedMetersPerSecond);

internal sealed record DetailDto(
    Guid Id, string ExternalId, string Sport, DateTimeOffset StartTime, double DistanceMeters, double ElapsedSeconds,
    double? ElevationGainMeters, double MovingSeconds, SummaryDto Summary, IReadOnlyList<LapDto> Laps, StreamDto Stream);

/// <summary>Risposta HTTP gia' letta.</summary>
internal sealed record ApiResponse(HttpStatusCode Status, string Body, string? MediaType)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public JsonElement Document => JsonDocument.Parse(Body).RootElement;

    public Guid UploadId => Document.GetProperty("uploadId").GetGuid();

    public string UploadStatus => Document.GetProperty("status").GetString()!;

    public string ExternalId => Document.GetProperty("externalId").GetString()!;

    public string ProblemType => Document.GetProperty("type").GetString()!;

    public string ProblemDetail => Document.GetProperty("detail").GetString()!;

    public ListPage AsList() => JsonSerializer.Deserialize<ListPage>(Body, Json)!;

    public DetailDto AsDetail() => JsonSerializer.Deserialize<DetailDto>(Body, Json)!;

    public static async Task<ApiResponse> ReadAsync(HttpResponseMessage response) =>
        new(response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType);

    /// <summary>Corpo senza i campi che variano da una richiesta all'altra (<c>traceId</c>), per confrontare due risposte di errore.</summary>
    public string StableBody()
    {
        if (Body.Length == 0)
        {
            return string.Empty;
        }

        var properties = Document.EnumerateObject().Where(p => p.Name is not "traceId").Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join(";", properties);
    }
}

/// <summary>Client HTTP che parla SOLO con un indirizzo (il gateway, oppure un servizio direttamente).</summary>
internal sealed class ServiceClient : IDisposable
{
    private readonly HttpClient _http;

    public ServiceClient(string baseAddress, TimeSpan? timeout = null)
    {
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = timeout ?? TimeSpan.FromMinutes(3),
        };
    }

    public string BaseAddress => _http.BaseAddress!.ToString();

    public async Task<ApiResponse> GetAsync(string path, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request);
        return await ApiResponse.ReadAsync(response);
    }

    public async Task<ApiResponse> UploadAsync(
        string path,
        string fileName,
        byte[] content,
        bool expectContinue = false,
        params (string Name, string Value)[] headers)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.ExpectContinue = expectContinue;
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request);
        return await ApiResponse.ReadAsync(response);
    }

    /// <summary>
    /// Come <see cref="UploadAsync"/> ma restituisce anche l'eccezione di rete: un rifiuto oltre il limite puo' arrivare come
    /// risposta 413 oppure come connessione interrotta mentre il client ancora scrive (limite di piattaforma, T14).
    /// </summary>
    public async Task<(ApiResponse? Response, HttpRequestException? Failure)> TryUploadAsync(
        string path, string fileName, byte[] content, bool expectContinue = false)
    {
        try
        {
            return (await UploadAsync(path, fileName, content, expectContinue), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Chiamate dello scenario per upload e lettura: tutte con il percorso del GATEWAY (<c>/api/...</c>).</summary>
internal sealed class GatewayApi(ServiceClient client)
{
    public const string UploadPath = "/api/ingestion/uploads";

    public ServiceClient Client { get; } = client;

    public Task<ApiResponse> UploadAsync(GeneratedFile file) => Client.UploadAsync(UploadPath, file.FileName, file.Content);

    public Task<ApiResponse> ListAsync(string query = "") => Client.GetAsync("/api/activities" + query);

    public Task<ApiResponse> DetailAsync(Guid id, string query = "") => Client.GetAsync($"/api/activities/{id}{query}");

    public async Task<ListPage> ListPageAsync(string query = "")
    {
        var response = await ListAsync(query);
        Assert.True(response.Status == HttpStatusCode.OK, $"GET /api/activities{query}: {(int)response.Status} {response.Body}");
        return response.AsList();
    }

    public async Task<DetailDto> DetailOfAsync(Guid id, string query = "")
    {
        var response = await DetailAsync(id, query);
        Assert.True(response.Status == HttpStatusCode.OK, $"GET /api/activities/{id}{query}: {(int)response.Status} {response.Body}");
        return response.AsDetail();
    }

    /// <summary>Attende che l'elenco contenga almeno <paramref name="count"/> attivita' (di norma una manciata di secondi).</summary>
    public Task<ListPage> WaitForTotalAsync(int count, TimeSpan? timeout = null, string query = "") =>
        Eventually.ValueAsync(
            () => ListPageAsync(query),
            page => page.Total >= count,
            timeout ?? Eventually.Long,
            $"l'elenco delle attivita' contiene almeno {count} elemento/i",
            page => $"{page.Total} attivita' ({string.Join(", ", page.Items.Select(i => i.ExternalId[..8]))})");

    /// <summary>Attende l'attivita' con quell'<c>externalId</c> (hash del file) e la restituisce.</summary>
    public async Task<ListItem> WaitForActivityAsync(string externalId, TimeSpan? timeout = null)
    {
        var page = await Eventually.ValueAsync(
            () => ListPageAsync("?pageSize=100"),
            p => p.Items.Any(i => i.ExternalId == externalId),
            timeout ?? Eventually.Long,
            $"l'attivita' {externalId[..8]} compare in elenco",
            p => $"{p.Total} attivita' ({string.Join(", ", p.Items.Select(i => i.ExternalId[..8]))})");
        return page.Items.Single(i => i.ExternalId == externalId);
    }
}
