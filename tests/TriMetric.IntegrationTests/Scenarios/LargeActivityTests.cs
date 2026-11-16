using System.Diagnostics;
using System.Net;
using TriMetric.Contracts;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;
using Xunit.Abstractions;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>
/// AC-014 (REQ-035, REQ-036, DEC-012): 50.000 e 100.000 punti con tutti i campi sono importati con lo stream completo nel database,
/// l'evento da 100.000 punti sta sotto 8 MB, 100.001 punti sono rifiutati. I file si generano in memoria (nessun file nel repository).
/// </summary>
[Collection(E2eCollection.Name)]
public sealed class LargeActivityTests(TimescaleFixture db, NatsFixture nats, ITestOutputHelper output)
{
    private const int EventSizeLimitBytes = 8_000_000;

    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public async Task Ac014_LongActivity_IsImported_WithTheFullStreamInTheDatabase_AndAReducedDetail(int points)
    {
        // Il parsing di ingestion ha un tempo massimo di 30 s (predefinito): sotto Trace (tutti i log catturati) e con tre servizi e due
        // contenitori sulla stessa macchina un file da 100.000 punti potrebbe avvicinarlo. Qui si prova l'import, non la velocita':
        // per questi due test il tetto e' 120 s (Upload:ParseTimeoutSeconds, lettura alla prima risoluzione: va bene UseSetting).
        var options = new SystemOptions { Ingestion = new Dictionary<string, string> { ["Upload:ParseTimeoutSeconds"] = "120" } };
        await using var system = await EndToEndSystem.StartAsync(db, nats, options);

        // Coordinate con 9 decimali, quota con 3 e velocita' con 6: piu' di quanto l'evento puo' portare (DEC-012). Passo di 1,001 s,
        // cosi' anche gli scarti di tempo hanno 3 decimali: il caso peggiore per la dimensione dell'evento.
        var track = new GpxTrack
        {
            Seed = Guid.NewGuid(),
            Points = points,
            Start = new DateTimeOffset(2024, 7, 1, 5, 0, 0, TimeSpan.Zero),
            StepMilliseconds = 1001,
            OverPrecise = true,
        };
        var generation = Stopwatch.StartNew();
        var file = track.Build();
        generation.Stop();
        Assert.True(file.Content.Length < 33_554_432, $"il file generato ({file.Content.Length} byte) deve stare sotto il limite di upload di 32 MiB");
        output.WriteLine($"{points} punti: file di {file.Content.Length:N0} byte ({file.Content.Length / 1048576.0:0.0} MiB), generato in {generation.ElapsedMilliseconds} ms.");

        var importing = Stopwatch.StartNew();
        var response = await system.UploadAsync(file);
        Expect.Status(HttpStatusCode.Accepted, response);
        var uploadTime = importing.Elapsed;
        var item = await system.Api.WaitForActivityAsync(file.Sha256);
        importing.Stop();
        output.WriteLine($"Risposta 202 dopo {uploadTime.TotalMilliseconds:0} ms; comparsa in elenco dopo {importing.Elapsed.TotalMilliseconds:0} ms.");
        using var process = Process.GetCurrentProcess();
        output.WriteLine($"Memoria del processo di test (tre servizi inclusi): picco working set {process.PeakWorkingSet64 / 1048576.0:0} MiB, managed {GC.GetTotalMemory(false) / 1048576.0:0} MiB.");

        // L'elenco ha i valori attesi dal file.
        Assert.Equal("run", item.Sport);
        Expect.SameInstant(track.ExpectedStart, item.StartTime, "inizio");
        Expect.CloseRelative(track.ExpectedDistanceMeters, item.DistanceMeters, 1e-4, "distanza");
        Expect.Close(track.ExpectedElapsedSeconds, item.ElapsedSeconds, 0.001, "durata");
        Expect.Close(track.ExpectedElevationGainMeters, item.ElevationGainMeters, 0.01, "dislivello");

        // Il database ha TUTTI i punti, con i valori arrotondati come richiede DEC-012.
        Assert.Equal((long)points, await system.Db.StreamPointCountAsync(item.Id));
        var indices = new[] { 0, 1, 2, 3, 999, 12_345, points / 2, points - 2, points - 1 }.Distinct().ToArray();
        var rows = await system.Db.StreamAsync(item.Id, indices.Select(i => track.TimeOf(i).UtcDateTime));
        Assert.Equal(indices.Length, rows.Count);
        foreach (var (row, i) in rows.Zip(indices.OrderBy(i => track.OffsetMs(i))))
        {
            Expect.Close(track.ExpectedLatitude(i), row.Latitude, 1e-10, $"DB: latitudine del punto {i} (arrotondata a 7 decimali)");
            Expect.Close(track.ExpectedLongitude(i), row.Longitude, 1e-10, $"DB: longitudine del punto {i}");
            Expect.Close(track.ExpectedAltitude(i), row.Altitude, 1e-9, $"DB: quota del punto {i} (arrotondata a 0,1 m)");
            Expect.Close(track.HeartRate(i), row.HeartRate, 0, $"DB: FC del punto {i}");
            Expect.Close(track.Power(i), row.Power, 0, $"DB: potenza del punto {i}");
            Expect.Close(track.Cadence(i), row.Cadence, 0, $"DB: cadenza del punto {i}");
            Expect.Close(track.ExpectedSpeed(i), row.Speed, 1e-9, $"DB: velocita' del punto {i} (arrotondata a 3 decimali)");
        }

        // Il dettaglio, con il limite predefinito, ha al massimo 1000 punti, compresi il primo e l'ultimo.
        var detail = await system.Api.DetailOfAsync(item.Id);
        var stream = detail.Stream;
        Assert.InRange(stream.TimeOffsetSeconds.Count, 2, 1000);
        Assert.Equal(1000, stream.TimeOffsetSeconds.Count);
        foreach (var column in new[] { stream.Latitude, stream.Longitude, stream.AltitudeMeters, stream.HeartRate, stream.PowerWatts, stream.Cadence, stream.SpeedMetersPerSecond })
        {
            Assert.NotNull(column);
            Assert.Equal(stream.TimeOffsetSeconds.Count, column!.Count);
        }

        Expect.Close(0, stream.TimeOffsetSeconds[0], 0.0005, "primo punto: scarto di tempo");
        Expect.Close(track.ExpectedElapsedSeconds, stream.TimeOffsetSeconds[^1], 0.0005, "ultimo punto: scarto di tempo");
        Expect.Close(track.ExpectedLatitude(0), stream.Latitude![0], 1e-10, "primo punto: latitudine");
        Expect.Close(track.ExpectedLatitude(points - 1), stream.Latitude![^1], 1e-10, "ultimo punto: latitudine");
        Assert.True(stream.TimeOffsetSeconds.Zip(stream.TimeOffsetSeconds.Skip(1)).All(p => p.First <= p.Second), "gli scarti di tempo sono non decrescenti");

        // L'evento pubblicato (misurato dal server NATS sul messaggio nello stream) sta sotto 8.000.000 byte.
        var published = Assert.Single(await system.NatsProbe.EventsAsync(withPayload: false));
        output.WriteLine($"Evento con {points} punti nello stream EVENTS: {published.SizeBytes:N0} byte (limite 8.000.000; max_payload del server 8.388.608).");
        Assert.InRange(published.SizeBytes, points * 30, EventSizeLimitBytes - 1);
        Assert.Equal(1, await system.NatsProbe.EventsCountAsync());
        Assert.Equal(0, await system.NatsProbe.DlqCountAsync());

        system.AssertLogsContainNoPersonalData();
    }

    [Fact]
    public async Task Ac014_OneHundredThousandAndOnePoints_IsRejected_WithNoEventNoRows()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var track = new GpxTrack
        {
            Seed = Guid.NewGuid(),
            Points = StreamLimits.MaxPoints + 1,
            Start = new DateTimeOffset(2024, 7, 2, 5, 0, 0, TimeSpan.Zero),
        };
        var file = track.Build();
        Assert.Equal(100_001, track.Points);
        Assert.True(file.Content.Length < 33_554_432);   // sotto il limite di dimensione: il rifiuto e' per i punti, non per i byte

        var response = await system.UploadAsync(file);

        Expect.Status(HttpStatusCode.UnprocessableEntity, response);
        Assert.Equal("application/problem+json", response.MediaType);
        Assert.Equal("urn:trimetric:problem:too-many-points", response.ProblemType);
        Assert.Contains("100000", response.ProblemDetail);
        Assert.Equal(0, (await system.Api.ListPageAsync()).Total);
        Assert.Equal(0, await system.NatsProbe.EventsCountAsync());
        Assert.Equal(0L, await system.Db.UploadCountAsync());
        Assert.Empty(await system.Db.OutboxAsync());
        Assert.Equal(0L, await system.Db.ActivityCountAsync());
        system.AssertLogsContainNoPersonalData();
    }
}
