using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;
using Xunit.Abstractions;

namespace TriMetric.Activities.Tests;

/// <summary>
/// <c>GET /activities/{id}</c> (T13; REQ-018; AC-011): campi del dettaglio, giri ordinati, stream ridotto a passo costante con
/// primo e ultimo punto, scarti di tempo esatti, limiti di <c>maxPoints</c> e configurazione del massimo.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Api")]
public sealed class ActivityDetailApiTests(TimescaleFixture db, NatsFixture nats, ITestOutputHelper output)
{
    /// <summary>Inizio con offset +02:00 e frazione di secondo (0,123456 s): conta l'istante UTC.</summary>
    private static readonly DateTimeOffset Start = ImportedEvents.Start;

    private async Task<(Guid User, SeedActivity Activity)> SeedAsync(StreamColumns? stream, Func<SeedActivity, SeedActivity>? change = null)
    {
        var user = Guid.NewGuid();
        var activity = SeedActivity.New(user, Sport.Ride, Start, stream);
        activity = change?.Invoke(activity) ?? activity;
        await ApiData.InsertAsync(db, activity);
        return (user, activity);
    }

    [Fact]
    public async Task Detail_ReturnsEveryField_LapsOrderedByStart_AndTheFullStreamWithNulls()
    {
        var stream = StreamTestData.Build(40, allColumns: false);   // cadenza e quota assenti, null dentro le colonne
        var laps = new List<LapData>
        {
            new(Start.AddMinutes(20), 600.5, 2001.25),
            new(Start, 600.25, 2000.5),
            new(Start.AddMinutes(10).ToOffset(TimeSpan.FromHours(-5)), 601.75, 1999.75),
        };
        var (user, activity) = await SeedAsync(stream, a => a with
        {
            Sport = Sport.Run,
            Summary = new ActivitySummary(148.5, null, 230.5, null),
            Laps = laps,
            ElevationGainMeters = null,
        });
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var detail = await ApiData.DetailAsync(client, activity.Id);

        Assert.Equal(activity.Id, detail.Id);
        Assert.Equal(activity.ExternalId, detail.ExternalId);
        Assert.Equal(Sport.Run, detail.Sport);
        Assert.Equal(Start.UtcTicks, detail.StartTime.UtcTicks);
        Assert.Equal(TimeSpan.Zero, detail.StartTime.Offset);
        Assert.Equal(activity.DistanceMeters, detail.DistanceMeters);
        Assert.Equal(activity.ElapsedSeconds, detail.ElapsedSeconds);
        Assert.Equal(activity.MovingSeconds, detail.MovingSeconds);
        Assert.Null(detail.ElevationGainMeters);
        Assert.Equal(new ActivitySummary(148.5, null, 230.5, null), detail.Summary);
        Assert.Equal(
            laps.OrderBy(l => l.StartTime.UtcTicks).Select(l => (l.StartTime.UtcTicks, l.ElapsedSeconds, l.DistanceMeters)),
            detail.Laps.Select(l => (l.StartTime.UtcTicks, l.ElapsedSeconds, l.DistanceMeters)));
        Assert.All(detail.Laps, l => Assert.Equal(TimeSpan.Zero, l.StartTime.Offset));
        ApiData.AssertColumnsAt(stream, detail.Stream, [.. Enumerable.Range(0, 40)]);
        Assert.Null(detail.Stream.Cadence);
        Assert.Null(detail.Stream.AltitudeMeters);
        Assert.Contains(detail.Stream.HeartRate!, v => v is null);
    }

    [Fact]
    public async Task Detail_JsonShape_HasTheContractFields_AndNoInternalOnes()
    {
        var (user, activity) = await SeedAsync(StreamTestData.Build(3, allColumns: false), a => a with { Sport = Sport.Other });
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        using var document = JsonDocument.Parse(await client.GetStringAsync($"/activities/{activity.Id}"));
        var root = document.RootElement;

        Assert.Equal(
            ["distanceMeters", "elapsedSeconds", "elevationGainMeters", "externalId", "id", "laps", "movingSeconds", "sport", "startTime", "stream", "summary"],
            root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("other", root.GetProperty("sport").GetString());
        Assert.Equal(
            ["altitudeMeters", "cadence", "heartRate", "latitude", "longitude", "powerWatts", "speedMetersPerSecond", "timeOffsetSeconds"],
            root.GetProperty("stream").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("stream").GetProperty("cadence").ValueKind);
        Assert.Equal(
            ["avgCadence", "avgHeartRate", "avgPowerWatts", "maxHeartRate"],
            root.GetProperty("summary").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task FiftyThousandPoints_DefaultMaxPoints_AtMost1000_FirstAndLastExact_ConstantStep_SameIndicesForEveryColumn()
    {
        const int n = 50_000;
        var stream = StreamTestData.Build(n, allColumns: true);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var detail = await ApiData.DetailAsync(client, activity.Id);

        var returned = detail.Stream;
        Assert.True(returned.TimeOffsetSeconds.Count <= 1000, $"{returned.TimeOffsetSeconds.Count} punti");
        Assert.Equal(1000, returned.TimeOffsetSeconds.Count);
        var indices = ApiData.IndicesOf(returned);
        Assert.Equal(0, indices[0]);
        Assert.Equal(n - 1, indices[^1]);
        Assert.Equal(stream.TimeOffsetSeconds[0], returned.TimeOffsetSeconds[0]);
        Assert.Equal(stream.TimeOffsetSeconds[n - 1], returned.TimeOffsetSeconds[^1]);

        // Passo costante: tutte le differenze di indice valgono floor o ceil di (N-1)/(m-1).
        var steps = indices.Zip(indices.Skip(1), (a, b) => b - a).Distinct().ToArray();
        var ideal = (n - 1) / 999.0;
        Assert.All(steps, s => Assert.True(s == (int)Math.Floor(ideal) || s == (int)Math.Ceiling(ideal), $"passo {s} (ideale {ideal})"));
        Assert.Equal(ApiData.ExpectedIndices(n, 1000).Select(i => (int)i), indices);
        ApiData.AssertColumnsAt(stream, returned, indices);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 1000)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(2, 1000)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 3)]
    [InlineData(10, 4)]
    [InlineData(9, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(1999, 1000)]
    public async Task Reduction_PicksTheDeterministicIndices(int n, int maxPoints)
    {
        var stream = StreamTestData.Build(n, allColumns: true);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var detail = await ApiData.DetailAsync(client, activity.Id, $"?maxPoints={maxPoints}");

        var expected = ApiData.ExpectedIndices(n, maxPoints).Select(i => (int)i).ToList();
        Assert.Equal(Math.Min(n, maxPoints), detail.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(expected, ApiData.IndicesOf(detail.Stream));
        Assert.Equal(expected.Count, expected.Distinct().Count());
        if (n > 0)
        {
            ApiData.AssertColumnsAt(stream, detail.Stream, expected);
            Assert.Equal(0, expected[0]);
            Assert.Equal(n - 1, expected[^1]);
        }
        else
        {
            // Nessuna riga: una colonna vuota non e' distinguibile da una assente e torna null.
            Assert.Null(detail.Stream.Latitude);
            Assert.Null(detail.Stream.HeartRate);
        }
    }

    [Fact]
    public async Task ActivityWithoutPoints_ReturnsAnEmptyStream_WithNullColumns()
    {
        var (user, activity) = await SeedAsync(stream: null);   // nessuna riga nello stream (il gestore di T12 lo accetta)
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        using var document = JsonDocument.Parse(await client.GetStringAsync($"/activities/{activity.Id}"));
        var stream = document.RootElement.GetProperty("stream");

        Assert.Equal(0, stream.GetProperty("timeOffsetSeconds").GetArrayLength());
        foreach (var column in ImportedEvents.ValueColumns)
        {
            Assert.Equal(JsonValueKind.Null, stream.GetProperty(column).ValueKind);
        }
    }

    [Fact]
    public async Task TimeOffsets_AreExact_WithAStartAtPlusTwoHours()
    {
        double[] offsets = [0, 0.001, 12.345, 3600.5, 7200.999, 86_399.999];
        var stream = new StreamColumns(offsets, null, null, null, [.. offsets.Select(_ => (double?)150)], null, null, null);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var detail = await ApiData.DetailAsync(client, activity.Id);

        Assert.Equal(offsets, detail.Stream.TimeOffsetSeconds);
        Assert.Null(detail.Stream.Latitude);
        Assert.Equal(offsets.Select(_ => (double?)150), detail.Stream.HeartRate!);
    }

    [Fact]
    public async Task PartiallyNullColumns_StayPresent_EvenWhenTheChosenPointsAreAllNull()
    {
        // Frequenza cardiaca solo nel punto centrale: con maxPoints=2 si scelgono il primo e l'ultimo (entrambi null), ma la
        // colonna esiste nello stream e resta un array di null, non null. La potenza ha null all'inizio.
        double[] offsets = [0, 1, 2, 3, 4];
        var stream = new StreamColumns(
            offsets, null, null, null,
            HeartRate: [null, null, 140, null, null],
            PowerWatts: [null, 200, 210, 220, 230],
            Cadence: null,
            SpeedMetersPerSecond: null);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var reduced = await ApiData.DetailAsync(client, activity.Id, "?maxPoints=2");
        var full = await ApiData.DetailAsync(client, activity.Id);

        Assert.Equal([0, 4], reduced.Stream.TimeOffsetSeconds);
        Assert.Equal([null, null], reduced.Stream.HeartRate!);
        Assert.Equal([null, 230], reduced.Stream.PowerWatts!);
        Assert.Null(reduced.Stream.Latitude);
        Assert.Equal(stream.HeartRate, full.Stream.HeartRate!);
        Assert.Equal(stream.PowerWatts, full.Stream.PowerWatts!);
    }

    [Theory]
    [InlineData("?maxPoints=1")]
    [InlineData("?maxPoints=0")]
    [InlineData("?maxPoints=-5")]
    [InlineData("?maxPoints=abc")]
    [InlineData("?maxPoints=1.5")]
    [InlineData("?maxPoints=")]
    [InlineData("?maxPoints=5001")]
    [InlineData("?maxPoints=100000")]
    [InlineData("?maxPoints=2&maxPoints=3")]
    public async Task InvalidMaxPoints_Returns400(string query)
    {
        var (user, activity) = await SeedAsync(StreamTestData.Build(10));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        await ApiData.AssertProblemAsync(await client.GetAsync($"/activities/{activity.Id}{query}"), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5000)]
    public async Task MaxPoints_AtTheLimits_IsAccepted(int maxPoints)
    {
        var (user, activity) = await SeedAsync(StreamTestData.Build(10));
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var detail = await ApiData.DetailAsync(client, activity.Id, $"?maxPoints={maxPoints}");

        Assert.Equal(Math.Min(10, maxPoints), detail.Stream.TimeOffsetSeconds.Count);
    }

    [Fact]
    public async Task ConfiguredMaximum_MovesTheLimit()
    {
        var (user, activity) = await SeedAsync(StreamTestData.Build(30_000, allColumns: false));
        var settings = new Dictionary<string, string?> { ["Activities:MaxStreamPoints"] = "20000" };
        await using var host = ActivitiesHost.Development(db, nats, userId: user, startupSettings: settings);
        using var client = host.Start();

        Assert.Equal(20_000, (await ApiData.DetailAsync(client, activity.Id, "?maxPoints=20000")).Stream.TimeOffsetSeconds.Count);
        await ApiData.AssertProblemAsync(await client.GetAsync($"/activities/{activity.Id}?maxPoints=20001"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfiguredMaximum_EqualToTheDefault_RejectsAboveIt()
    {
        var (user, activity) = await SeedAsync(StreamTestData.Build(10));
        var settings = new Dictionary<string, string?> { ["Activities:MaxStreamPoints"] = "1000" };
        await using var host = ActivitiesHost.Development(db, nats, userId: user, startupSettings: settings);
        using var client = host.Start();

        Assert.Equal(10, (await ApiData.DetailAsync(client, activity.Id)).Stream.TimeOffsetSeconds.Count);
        await ApiData.AssertProblemAsync(await client.GetAsync($"/activities/{activity.Id}?maxPoints=1001"), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Activities:MaxStreamPoints", "abc")]
    [InlineData("Activities:MaxStreamPoints", "999")]
    [InlineData("Activities:MaxStreamPoints", "0")]
    [InlineData("Activities:MaxStreamPoints", "-1")]
    [InlineData("Activities:MaxStreamPoints", "100001")]
    [InlineData("Activities:MaxStreamPoint", "2000")]
    public async Task InvalidConfiguration_FailsAtStartup_WithAClearMessage(string key, string value)
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(
            db, nats, userId: Guid.NewGuid(), startupSettings: new Dictionary<string, string?> { [key] = value });

        var error = Record.Exception(() => host.Start());

        Assert.NotNull(error);
        var messages = string.Join(" | ", Chain(error));
        Assert.Contains("MaxStreamPoint", messages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOrMalformedId_Returns404()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var host = ActivitiesHost.Development(db, nats, userId: Guid.NewGuid());
        using var client = host.Start();

        await ApiData.AssertProblemAsync(await client.GetAsync($"/activities/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/activities/non-un-guid")).StatusCode);
    }

    [Fact]
    public async Task Measure_HundredThousandPointsWithAllColumns_DefaultMaxPoints()
    {
        const int n = StreamLimits.MaxPoints;
        var stream = StreamTestData.Build(n, allColumns: true);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();
        _ = await ApiData.DetailAsync(client, activity.Id, "?maxPoints=2");   // riscaldamento (JIT, pool, piano)

        var watch = Stopwatch.StartNew();
        var detail = await ApiData.DetailAsync(client, activity.Id);
        watch.Stop();

        output.WriteLine($"Dettaglio con {n} punti e 8 colonne, maxPoints predefinito: {watch.ElapsedMilliseconds} ms, {detail.Stream.TimeOffsetSeconds.Count} punti restituiti.");
        Assert.Equal(1000, detail.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(stream.TimeOffsetSeconds[^1], detail.Stream.TimeOffsetSeconds[^1]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task EqualTimeOffsets_AreAllReturned_OrderedByLatitude_AndTheReductionIsRepeatable()
    {
        // Il contratto ammette scarti uguali: tra punti allo stesso istante l'ordine e' deterministico per valori (prima colonna
        // dello spareggio: latitudine crescente) e due richieste identiche scelgono gli stessi punti. Qui la latitudine cresce
        // con la posizione nel file, quindi l'ordine atteso coincide con quello di scrittura (non e' una garanzia del prodotto:
        // vedi il test con latitudini decrescenti).
        const int n = 40;
        var offsets = Enumerable.Range(0, n).Select(i => (double)(i / 4)).ToArray();   // quattro punti per ogni istante
        var latitude = Enumerable.Range(0, n).Select(i => (double?)(1000 + i)).ToArray();
        var stream = new StreamColumns(offsets, latitude, null, null, null, null, null, null);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var all = await ApiData.DetailAsync(client, activity.Id);
        Assert.Equal(offsets, all.Stream.TimeOffsetSeconds);
        Assert.Equal(latitude, all.Stream.Latitude);   // tutti e 40, per istante e poi per latitudine crescente

        var first = await ApiData.DetailAsync(client, activity.Id, "?maxPoints=7");
        var second = await ApiData.DetailAsync(client, activity.Id, "?maxPoints=7");
        Assert.Equal(7, first.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(first.Stream.Latitude, second.Stream.Latitude);
        Assert.Equal(1000.0, first.Stream.Latitude![0]);
        Assert.Equal(1000.0 + n - 1, first.Stream.Latitude![^1]);
        Assert.Equal(ApiData.ExpectedIndices(n, 7).Select(i => (double?)(1000 + i)), first.Stream.Latitude);
    }

    [Fact]
    public async Task EqualTimeOffsets_AreOrderedByValues_NotByWriteOrder_NullsLast_ForFullAndReducedReads()
    {
        // Contratto: a parita' di istante i punti si ordinano per latitude, longitude, altitude_meters, heart_rate, power_watts,
        // cadence, speed_mps (crescenti, NULL per ultimi); l'ordine del file e l'ordine fisico (ctid) non contano. Ogni istante ha
        // cinque punti SCRITTI nell'ordine A,B,C,D,E, opposto a quello atteso D,C,B,E,A:
        //   A lat=null hr=5    B lat=3 hr=null    C lat=3 hr=2    D lat=2 hr=9    E lat=null hr=1
        // D prima (latitudine minore), C prima di B (stessa latitudine, FC 2 prima di null), E prima di A (latitudine null,
        // FC 1 prima di 5). La cadenza identifica il punto (indice di scrittura) e non influisce sull'ordine.
        const int instants = 10;
        const int perInstant = 5;
        const int n = instants * perInstant;
        int[] expectedWithinInstant = [3, 2, 1, 4, 0];   // indici di scrittura D,C,B,E,A
        double?[] lat = [null, 3, 3, 2, null];
        double?[] hr = [5, null, 2, 9, 1];
        var offsets = Enumerable.Range(0, n).Select(i => (double)(i / perInstant)).ToArray();
        var latitude = Enumerable.Range(0, n).Select(i => lat[i % perInstant] is { } l ? (double?)(l + (10 * (i / perInstant))) : null).ToArray();
        var heartRate = Enumerable.Range(0, n).Select(i => hr[i % perInstant]).ToArray();
        var cadence = Enumerable.Range(0, n).Select(i => (double?)i).ToArray();
        var stream = new StreamColumns(offsets, latitude, null, null, heartRate, null, cadence, null);
        var (user, activity) = await SeedAsync(stream);
        await using var host = ActivitiesHost.Development(db, nats, userId: user);
        using var client = host.Start();

        var expected = Enumerable.Range(0, n)
            .Select(i => (i / perInstant * perInstant) + expectedWithinInstant[i % perInstant])   // indice di scrittura atteso alla posizione i
            .ToArray();

        var all = await ApiData.DetailAsync(client, activity.Id);
        Assert.Equal(offsets, all.Stream.TimeOffsetSeconds);
        Assert.Equal(expected.Select(i => (double?)i), all.Stream.Cadence);
        Assert.Equal(expected.Select(i => latitude[i]), all.Stream.Latitude);
        Assert.Equal(expected.Select(i => heartRate[i]), all.Stream.HeartRate);

        // Riduzione: stessi indici di sempre applicati alla sequenza ORDINATA PER VALORI, identica nella lettura ridotta.
        const int maxPoints = 9;
        var picked = ApiData.ExpectedIndices(n, maxPoints).Select(k => expected[(int)k]).ToArray();
        var first = await ApiData.DetailAsync(client, activity.Id, $"?maxPoints={maxPoints}");
        var second = await ApiData.DetailAsync(client, activity.Id, $"?maxPoints={maxPoints}");
        Assert.Equal(maxPoints, first.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(picked.Select(i => (double?)i), first.Stream.Cadence);
        Assert.Equal(picked.Select(i => latitude[i]), first.Stream.Latitude);
        Assert.Equal(picked.Select(i => heartRate[i]), first.Stream.HeartRate);
        Assert.Equal(first.Stream.Cadence, second.Stream.Cadence);
        Assert.Equal(first.Stream.TimeOffsetSeconds, second.Stream.TimeOffsetSeconds);
        Assert.Equal(expected[0], first.Stream.Cadence![0]);
        Assert.Equal(expected[^1], first.Stream.Cadence![^1]);
    }

    private static IEnumerable<string> Chain(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e.Message;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    foreach (var message in Chain(inner))
                    {
                        yield return message;
                    }
                }
            }
        }
    }
}
