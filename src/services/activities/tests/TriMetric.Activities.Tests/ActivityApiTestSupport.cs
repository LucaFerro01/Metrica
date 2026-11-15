using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Activities.Tests;

/// <summary>Attivita' da inserire direttamente nel database per i test delle API (T13), senza passare da NATS.</summary>
internal sealed record SeedActivity(
    Guid Id,
    Guid UserId,
    string ExternalId,
    Sport Sport,
    DateTimeOffset StartTime,
    double DistanceMeters = 10234.75,
    double ElapsedSeconds = 3725.5,
    double MovingSeconds = 3600.25,
    double? ElevationGainMeters = 123.4,
    ActivitySummary? Summary = null,
    IReadOnlyList<LapData>? Laps = null,
    StreamColumns? Stream = null,
    string Source = "file")
{
    /// <summary>Attivita' con identificativi nuovi (id ed <c>externalId</c>): nessun test dipende da un altro.</summary>
    public static SeedActivity New(Guid userId, Sport sport, DateTimeOffset start, StreamColumns? stream = null) =>
        new(Guid.NewGuid(), userId, $"hash-{Guid.NewGuid():N}", sport, start, Stream: stream);
}

/// <summary>
/// Scrittura dei dati di prova come ruolo <c>activities_svc</c> (stesso modello EF e stesso <see cref="StreamWriter"/> del
/// servizio) e lettura delle risposte delle API. Il database e' condiviso nella collection: ogni test usa utenti nuovi.
/// </summary>
internal static class ApiData
{
    /// <summary>Opzioni JSON per leggere le risposte: camelCase e <see cref="Sport"/> come stringa minuscola.</summary>
    public static readonly JsonSerializerOptions Json = CreateJson();

    public static async Task InsertAsync(TimescaleFixture db, params SeedActivity[] activities)
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var context = MigratedDatabase.NewContext(db);
        await using var transaction = await context.Database.BeginTransactionAsync();
        foreach (var a in activities)
        {
            context.Activities.Add(new Activity
            {
                Id = a.Id,
                UserId = a.UserId,
                Source = a.Source,
                ExternalId = a.ExternalId,
                Sport = a.Sport,
                StartTime = a.StartTime.ToUniversalTime(),
                ElapsedSeconds = a.ElapsedSeconds,
                MovingSeconds = a.MovingSeconds,
                DistanceMeters = a.DistanceMeters,
                ElevationGainMeters = a.ElevationGainMeters,
                AvgHeartRate = a.Summary?.AvgHeartRate,
                MaxHeartRate = a.Summary?.MaxHeartRate,
                AvgPowerWatts = a.Summary?.AvgPowerWatts,
                AvgCadence = a.Summary?.AvgCadence,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            foreach (var lap in a.Laps ?? [])
            {
                context.Laps.Add(new ActivityLap
                {
                    Id = Guid.NewGuid(),
                    ActivityId = a.Id,
                    StartTime = lap.StartTime.ToUniversalTime(),
                    ElapsedSeconds = lap.ElapsedSeconds,
                    DistanceMeters = lap.DistanceMeters,
                });
            }
        }

        await context.SaveChangesAsync();
        var writer = new StreamWriter(context);
        foreach (var a in activities.Where(a => a.Stream is not null))
        {
            await writer.WriteAsync(a.Id, a.StartTime, a.Stream!);
        }

        await transaction.CommitAsync();
    }

    /// <summary>Ordine atteso dell'elenco: <c>start_time</c> decrescente, poi <c>id</c> nell'ordine di PostgreSQL (byte = testo esadecimale).</summary>
    public static List<Guid> ExpectedOrder(IEnumerable<SeedActivity> activities) =>
        [.. activities
            .OrderByDescending(a => a.StartTime.UtcTicks)
            .ThenBy(a => a.Id.ToString("D"), StringComparer.Ordinal)
            .Select(a => a.Id)];

    public static async Task<ActivityListPage> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/activities" + query);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET /activities{query}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<ActivityListPage>(body, Json)!;
    }

    public static async Task<ActivityDetail> DetailAsync(HttpClient client, Guid id, string query = "")
    {
        var response = await client.GetAsync($"/activities/{id}{query}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET /activities/{{id}}{query}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<ActivityDetail>(body, Json)!;
    }

    /// <summary>Verifica una risposta <c>ProblemDetails</c> con lo stato atteso e la restituisce (senza <c>traceId</c>, che varia).</summary>
    public static async Task<string> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"atteso {(int)status}, ricevuto {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        var stable = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .Select(p => p.Name + "=" + p.Value.GetRawText());
        return string.Join("|", stable);
    }

    /// <summary>Indice di ciascun punto restituito nello stream originale (gli scarti di <see cref="StreamTestData.Build"/> sono unici).</summary>
    public static List<int> IndicesOf(StreamColumns returned) => [.. returned.TimeOffsetSeconds.Select(IndexOfOffset)];

    /// <summary>Indice del punto con lo scarto dato in <see cref="StreamTestData.Build"/> (<c>i * 1000 + (i % 7) * 137</c> ms).</summary>
    public static int IndexOfOffset(double offsetSeconds)
    {
        var ms = (long)Math.Round(offsetSeconds * 1000);
        var i = (int)(ms / 1000);
        for (var candidate = Math.Max(0, i - 1); candidate <= i; candidate++)
        {
            if (StreamTestData.OffsetMilliseconds(candidate) == ms)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Scarto {offsetSeconds} non prodotto da StreamTestData.Build.");
    }

    /// <summary>Indici attesi della riduzione: <c>round(k*(N-1)/(m-1))</c> per k=0..m-1, m=min(N, maxPoints) (meta' verso l'alto).</summary>
    public static List<long> ExpectedIndices(long n, int maxPoints)
    {
        var m = Math.Min(n, maxPoints);
        if (m == 0)
        {
            return [];
        }

        if (m == 1)
        {
            return [0];
        }

        return [.. Enumerable.Range(0, (int)m).Select(k => (long)Math.Floor((decimal)k * (n - 1) / (m - 1) + 0.5m))];
    }

    /// <summary>Confronta ogni colonna restituita con l'originale agli indici dati (stessi indici per tutte le colonne).</summary>
    public static void AssertColumnsAt(StreamColumns original, StreamColumns returned, IReadOnlyList<int> indices)
    {
        Assert.Equal(indices.Count, returned.TimeOffsetSeconds.Count);
        Assert.Equal(indices.Select(i => original.TimeOffsetSeconds[i]), returned.TimeOffsetSeconds);
        AssertColumn(nameof(StreamColumns.Latitude), original.Latitude, returned.Latitude, indices);
        AssertColumn(nameof(StreamColumns.Longitude), original.Longitude, returned.Longitude, indices);
        AssertColumn(nameof(StreamColumns.AltitudeMeters), original.AltitudeMeters, returned.AltitudeMeters, indices);
        AssertColumn(nameof(StreamColumns.HeartRate), original.HeartRate, returned.HeartRate, indices);
        AssertColumn(nameof(StreamColumns.PowerWatts), original.PowerWatts, returned.PowerWatts, indices);
        AssertColumn(nameof(StreamColumns.Cadence), original.Cadence, returned.Cadence, indices);
        AssertColumn(nameof(StreamColumns.SpeedMetersPerSecond), original.SpeedMetersPerSecond, returned.SpeedMetersPerSecond, indices);
    }

    private static void AssertColumn(string name, IReadOnlyList<double?>? original, IReadOnlyList<double?>? returned, IReadOnlyList<int> indices)
    {
        if (original is null)
        {
            Assert.True(returned is null, $"La colonna {name} e' assente nell'originale ma non e' null nella risposta.");
            return;
        }

        Assert.True(returned is not null, $"La colonna {name} manca nella risposta.");
        Assert.True(indices.Select(i => original[i]).SequenceEqual(returned!), $"La colonna {name} non corrisponde agli indici scelti.");
    }

    public static HttpRequestMessage Get(string path, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
