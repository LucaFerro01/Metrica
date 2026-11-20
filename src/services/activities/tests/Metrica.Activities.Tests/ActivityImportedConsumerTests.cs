using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Metrica.Contracts;
using Metrica.TestInfrastructure;
using Xunit.Abstractions;

namespace Metrica.Activities.Tests;

/// <summary>
/// Consumo di <c>ActivityImported</c> con l'host reale, NATS e TimescaleDB reali (REQ-014, REQ-015, REQ-016, REQ-017; AC-006,
/// AC-012, AC-014; DEC-001, DEC-005). Ogni test usa utente, <c>externalId</c> ed evento nuovi, attese con polling e timeout
/// (mai attese fisse) e Backoff breve dove serve. L'isolamento dei consumatori e' descritto in <see cref="ImportScenario"/>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Consumer")]
[Trait("Category", "Persistence")]
public sealed class ActivityImportedConsumerTests(NatsFixture nats, TimescaleFixture db, ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string?> FastRetries = new()
    {
        ["Consumers:ActivityImported:BackoffMilliseconds:0"] = "100",
    };

    private Task<ImportScenario> NewScenarioAsync() => ImportScenario.CreateAsync(nats, db);

    // ---- Evento valido -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ValidEvent_IsSaved_ActivityLapsAndPoints_FieldByField()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var envelope = ImportedEvents.Valid(sport: Sport.Swim, points: 40, laps: 3);
        var data = envelope.Data;
        var before = DateTime.UtcNow;

        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato");

        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        var after = DateTime.UtcNow;
        Assert.Equal(envelope.UserId, activity.UserId);
        Assert.Equal(data.Source, activity.Source);
        Assert.Equal(data.ExternalId, activity.ExternalId);
        Assert.Equal("swim", activity.Sport);   // testo minuscolo (DEC-005), non "Swim"
        Assert.Equal(data.StartTime.UtcTicks, activity.StartTime.Ticks);   // lo stesso ISTANTE (l'evento ha offset +02:00)
        Assert.Equal(3725.5, activity.ElapsedSeconds);
        Assert.Equal(3600.25, activity.MovingSeconds);
        Assert.Equal(10234.75, activity.DistanceMeters);
        Assert.Equal(123.4, activity.ElevationGainMeters);
        Assert.Equal(148.5, activity.AvgHeartRate);
        Assert.Equal(181, activity.MaxHeartRate);
        Assert.Equal(230.5, activity.AvgPowerWatts);
        Assert.Equal(87, activity.AvgCadence);
        Assert.InRange(activity.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));   // created_at = ora UTC dell'elaborazione

        var laps = await s.LapsAsync(activity.Id);
        Assert.Equal(3, laps.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(activity.Id, laps[i].ActivityId);
            Assert.Equal(data.Laps[i].StartTime.UtcTicks, laps[i].StartTime.Ticks);
            Assert.Equal(data.Laps[i].ElapsedSeconds, laps[i].ElapsedSeconds);
            Assert.Equal(data.Laps[i].DistanceMeters, laps[i].DistanceMeters);
        }

        // Punti: time = inizio + scarto, colonne null (cadenza e quota assenti) e null dentro le colonne, riga per riga.
        var rows = await StreamTestData.ReadAsync(db, activity.Id);
        Assert.Equal(40, rows.Count);
        StreamTestData.AssertEqual(data.StartTime, data.Stream, activity.Id, rows);
        Assert.All(rows, r => Assert.Null(r.Altitude));
        Assert.All(rows, r => Assert.Null(r.Cadence));
        Assert.Contains(rows, r => r.Latitude is null);

        Assert.True(await s.InboxContainsAsync(envelope.Id));
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.DeadLettered);
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task ValidEvent_WithNullSummary_NoElevation_NoLaps_AndAllColumns_IsSaved()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var data = ImportedEvents.Data("file", $"hash-{Guid.NewGuid():N}", Sport.Other, points: 25, laps: 0, allColumns: true) with
        {
            ElevationGainMeters = null,
            Summary = new ActivitySummary(null, null, null, null),
        };
        var envelope = ImportedEvents.Envelope(data);

        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato");

        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        Assert.Equal("other", activity.Sport);
        Assert.Null(activity.ElevationGainMeters);
        Assert.Null(activity.AvgHeartRate);
        Assert.Null(activity.MaxHeartRate);
        Assert.Null(activity.AvgPowerWatts);
        Assert.Null(activity.AvgCadence);
        Assert.Empty(await s.LapsAsync(activity.Id));
        StreamTestData.AssertEqual(data.StartTime, data.Stream, activity.Id, await StreamTestData.ReadAsync(db, activity.Id));
    }

    [Theory]
    [InlineData(Sport.Run, "run")]
    [InlineData(Sport.Ride, "ride")]
    [InlineData(Sport.Swim, "swim")]
    [InlineData(Sport.Other, "other")]
    public async Task EverySport_IsSavedAsLowercaseText(Sport sport, string expected)
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var envelope = ImportedEvents.Valid(sport: sport, points: 5, laps: 1);

        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(() => ImportScenario.CountersOf(host).Processed >= 1, Timeout, "evento elaborato");

        Assert.Equal(expected, Assert.Single(await s.ActivitiesAsync(envelope.UserId)).Sport);
    }

    // ---- Idempotenza (AC-006) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SameEventDeliveredTwice_OneActivity_NoError_HandlerRunsOnce()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var envelope = ImportedEvents.Valid(points: 30, laps: 2);
        var payload = ImportedEvents.Serialize(envelope);

        // Due messaggi distinti nello stream (nessun Nats-Msg-Id: la deduplicazione di JetStream non interviene) con la stessa busta.
        await s.PublishRawAsync(payload);
        await s.PublishRawAsync(payload);

        await Eventually.AssertAsync(() => counters.Processed >= 1 && counters.Duplicates >= 1, Timeout, "duplicato riconosciuto");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "entrambi i messaggi confermati");

        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        Assert.Equal(1, s.Probe.ActivityInsertCount);   // il gestore e' girato una volta sola
        Assert.Equal(2, (await s.LapsAsync(activity.Id)).Count);
        Assert.Equal(30, await s.PointCountAsync(activity.Id));
        Assert.Equal(1, counters.Processed);
        Assert.Equal(1, counters.Duplicates);
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.DeadLettered);
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task SameNatsMsgId_IsDeduplicatedByJetStream_OneActivity()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var envelope = ImportedEvents.Valid(points: 10, laps: 1);

        var first = await s.PublishAsync(envelope, msgId: envelope.Id.ToString());
        var second = await s.PublishAsync(envelope, msgId: envelope.Id.ToString());

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato");
        Assert.Single(await s.ActivitiesAsync(envelope.UserId));
    }

    [Fact]
    public async Task RealRedelivery_OfAnAlreadyProcessedMessage_IsAnInboxDuplicate_NothingIsAdded()
    {
        await using var s = await NewScenarioAsync();
        var envelope = ImportedEvents.Valid(points: 30, laps: 2);
        Guid activityId;
        await using (var first = s.StartHost(FastRetries))
        {
            var counters = ImportScenario.CountersOf(first);
            await s.PublishAsync(envelope);
            await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "primo evento elaborato");
            activityId = Assert.Single(await s.ActivitiesAsync(envelope.UserId)).Id;
        }

        // Il durevole viene cancellato: il server riconsegna DAVVERO lo stesso messaggio (stessa sequenza, stessa busta) al
        // consumatore del nuovo host, come dopo la perdita dello stato del consumatore o di un Ack.
        await s.DeleteDurableAsync();
        await using var second = s.StartHost(FastRetries);
        var redelivered = ImportScenario.CountersOf(second);

        await Eventually.AssertAsync(() => redelivered.Received >= 1 && redelivered.Duplicates >= 1, Timeout, "messaggio riconsegnato e riconosciuto");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio confermato");

        Assert.Equal(0, redelivered.Processed);
        Assert.Equal(0, s.Probe.ActivityInsertCount);   // il gestore non e' ripartito
        Assert.Equal(activityId, Assert.Single(await s.ActivitiesAsync(envelope.UserId)).Id);
        Assert.Equal(2, (await s.LapsAsync(activityId)).Count);
        Assert.Equal(30, await s.PointCountAsync(activityId));
        Assert.Equal(0, redelivered.DeadLettered);
    }

    [Fact]
    public async Task TwoEventsWithDifferentIds_SameUserSourceAndExternalId_OneActivity_NoDuplicatedLapsOrPoints()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var userId = Guid.NewGuid();
        var externalId = $"hash-{Guid.NewGuid():N}";
        var first = ImportedEvents.Valid(userId, externalId: externalId, points: 30, laps: 2);
        // Stesso file, ma l'evento ripubblicato (id nuovo) con un contenuto diverso: la prima attivita' vince.
        var second = ImportedEvents.Valid(userId, externalId: externalId, points: 50, laps: 4, sport: Sport.Run);

        await s.PublishAsync(first);
        await s.PublishAsync(second);
        await Eventually.AssertAsync(() => counters.Processed >= 2, Timeout, "entrambi gli eventi elaborati");

        var activity = Assert.Single(await s.ActivitiesAsync(userId));
        Assert.Equal("ride", activity.Sport);   // i valori del primo evento
        Assert.Equal(2, (await s.LapsAsync(activity.Id)).Count);
        Assert.Equal(30, await s.PointCountAsync(activity.Id));
        Assert.True(await s.InboxContainsAsync(first.Id));
        Assert.True(await s.InboxContainsAsync(second.Id));   // il secondo evento e' stato elaborato (no-op), non scartato
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.DeadLettered);
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task SameExternalId_DifferentUser_OrDifferentSource_ProducesSeparateActivities()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var externalId = $"hash-{Guid.NewGuid():N}";
        var (userA, userB) = (Guid.NewGuid(), Guid.NewGuid());
        var a = ImportedEvents.Valid(userA, externalId: externalId, points: 10, laps: 1);
        var b = ImportedEvents.Valid(userB, externalId: externalId, points: 20, laps: 2);
        var otherSource = ImportedEvents.Valid(userA, source: "strava", externalId: externalId, points: 15, laps: 1);

        await s.PublishAsync(a);
        await s.PublishAsync(b);
        await s.PublishAsync(otherSource);
        await Eventually.AssertAsync(() => counters.Processed >= 3, Timeout, "tre eventi elaborati");

        var forB = Assert.Single(await s.ActivitiesAsync(userB));
        Assert.Equal(userB, forB.UserId);
        Assert.Equal(20, await s.PointCountAsync(forB.Id));
        Assert.Equal(2, (await s.LapsAsync(forB.Id)).Count);

        var forA = await s.ActivitiesAsync(userA);
        Assert.Equal(2, forA.Count);
        Assert.Equal(new[] { "file", "strava" }, forA.Select(x => x.Source).Order().ToArray());
        Assert.Equal(new long[] { 10, 15 }, await Task.WhenAll(forA.OrderBy(x => x.Source).Select(x => s.PointCountAsync(x.Id))));
    }

    /// <summary>
    /// LIMITE NOTO e dichiarato (vedi <see cref="ActivityImportedValidation"/>): un <c>sport</c> mancante diventa il valore predefinito
    /// dell'enum (<c>Run</c>) gia' alla deserializzazione e il gestore non puo' distinguerlo da un <c>run</c> esplicito.
    /// Il test fissa il comportamento reale: l'evento e' salvato come <c>run</c>, non va in DLQ.
    /// </summary>
    [Fact]
    public async Task SportMissingInTheJson_IsSavedAsRun_KnownLimitOfTheValidation()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var envelope = ImportedEvents.Valid(sport: Sport.Swim, points: 5, laps: 1);

        await s.PublishRawAsync(ImportedEvents.Mutate(envelope, r => ImportedEvents.DataOf(r).Remove("sport")));
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato");

        Assert.Equal("run", Assert.Single(await s.ActivitiesAsync(envelope.UserId)).Sport);
        Assert.Equal(0, counters.DeadLettered);
    }

    // ---- Isolamento tra i test della collection ---------------------------------------------------------------------------

    [Fact]
    public async Task ScenarioCleanup_LeavesNoMessagesNoDurableAndNoDlq_SoOtherTestsHostsFindNothingToConsume()
    {
        await using (var s = await NewScenarioAsync())
        {
            await using var host = s.StartHost(FastRetries);
            var counters = ImportScenario.CountersOf(host);
            await s.PublishAsync(ImportedEvents.Valid(points: 5, laps: 1));
            await s.PublishRawAsync(Mutated(r => ImportedEvents.DropLast(r, "heartRate")));
            await Eventually.AssertAsync(() => counters.Processed >= 1 && counters.DeadLettered >= 1, Timeout, "un evento elaborato e uno in DLQ");
        }   // prima l'host, poi lo scenario: purga e cancellazione del durevole

        await using var connection = new NatsConnection(new NatsOpts { Url = nats.Url });
        var js = new NatsJSContext(connection);
        var events = await js.GetStreamAsync(EventNames.EventsStream, new StreamInfoRequest { SubjectsFilter = ">" });
        var dlq = await js.GetStreamAsync(EventNames.DlqStream, new StreamInfoRequest { SubjectsFilter = ">" });

        Assert.False(events.Info.State.Subjects?.ContainsKey(EventNames.ActivityImportedSubject) ?? false, "messaggi rimasti su EVENTS");
        Assert.False(dlq.Info.State.Subjects?.ContainsKey($"dlq.{ImportScenario.Durable}") ?? false, "messaggi rimasti sulla DLQ");
        await Assert.ThrowsAsync<NatsJSApiException>(async () => await js.GetConsumerAsync(EventNames.EventsStream, ImportScenario.Durable));
    }

    // ---- Volume (AC-014) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EventWith100000Points_AndAllEightColumns_IsFullySaved_UnderTheNatsLimit()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);

        // Caso peggiore di DEC-012: valori con il massimo di cifre in ogni colonna (tempo a 0,001 s dalla costruzione dello stream).
        const int count = StreamLimits.MaxPoints;
        var baseStream = StreamTestData.Build(count);
        var worst = baseStream with
        {
            Latitude = Enumerable.Repeat<double?>(-123.1234567, count).ToArray(),
            Longitude = Enumerable.Repeat<double?>(-123.1234567, count).ToArray(),
            AltitudeMeters = Enumerable.Repeat<double?>(-1234.5, count).ToArray(),
            SpeedMetersPerSecond = Enumerable.Repeat<double?>(12.345, count).ToArray(),
            HeartRate = Enumerable.Repeat<double?>(255, count).ToArray(),
            PowerWatts = Enumerable.Repeat<double?>(2000, count).ToArray(),
            Cadence = Enumerable.Repeat<double?>(255, count).ToArray(),
        };
        var envelope = ImportedEvents.Envelope(ImportedEvents.Data("file", $"hash-{Guid.NewGuid():N}", Sport.Ride, 1, 5) with { Stream = worst });
        var payload = ImportedEvents.Serialize(envelope);
        output.WriteLine($"Evento con {count} punti e 8 colonne: {payload.Length:N0} byte (limite NATS 8.388.608).");
        Assert.True(payload.Length < 8 * 1024 * 1024, $"Evento di {payload.Length} byte: oltre gli 8 MB.");

        var watch = Stopwatch.StartNew();
        await s.PublishRawAsync(payload);
        await Eventually.AssertAsync(() => counters.Processed >= 1, TimeSpan.FromSeconds(60), "evento da 100.000 punti elaborato");
        watch.Stop();
        output.WriteLine($"Pubblicazione + elaborazione (consumatore, gestore, COPY, commit, Ack): {watch.Elapsed.TotalMilliseconds:N0} ms.");

        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        Assert.Equal(count, await s.PointCountAsync(activity.Id));
        var rows = await StreamTestData.ReadAsync(db, activity.Id);
        StreamTestData.AssertEqual(envelope.Data.StartTime, worst, activity.Id, rows);   // tutte le righe, tutte le colonne
        Assert.Equal(5, (await s.LapsAsync(activity.Id)).Count);
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.DeadLettered);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), $"Elaborazione di {watch.Elapsed.TotalSeconds:0.0} s.");
    }

    // ---- Eventi non validi -> DLQ senza ripetizioni (AC-012) -------------------------------------------------------------------

    private const string Validation = "Validation";
    private const string Malformed = "Malformed";

    private static readonly Dictionary<string, (string Reason, Func<byte[]> Payload)> InvalidCases = BuildInvalidCases();

    public static IEnumerable<object[]> InvalidCaseNames => InvalidCases.Keys.Select(k => new object[] { k });

    private static byte[] Mutated(Action<JsonObject> mutation) =>
        ImportedEvents.Mutate(ImportedEvents.Valid(allColumns: true), mutation);

    private static Dictionary<string, (string, Func<byte[]>)> BuildInvalidCases()
    {
        var cases = new Dictionary<string, (string, Func<byte[]>)>
        {
            // Colonne di lunghezza diversa da TimeOffsetSeconds (una per ogni colonna del contratto).
            ["timeOffsetSeconds-shorter"] = (Validation, () => Mutated(r => ImportedEvents.DropLast(r, "timeOffsetSeconds"))),
            ["timeOffsetSeconds-longer"] = (Validation, () => Mutated(r => ImportedEvents.StreamOf(r)["timeOffsetSeconds"]!.AsArray().Add(99999.5))),
            ["heartRate-longer"] = (Validation, () => Mutated(r => ImportedEvents.AppendNull(r, "heartRate"))),

            // Stream, Data e campi obbligatori.
            ["data-null"] = (Malformed, () => Mutated(r => r["data"] = null)),
            ["data-missing"] = (Malformed, () => Mutated(r => r.Remove("data"))),
            ["stream-null"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["stream"] = null)),
            ["stream-missing"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r).Remove("stream"))),
            ["timeOffsetSeconds-null"] = (Validation, () => Mutated(r => ImportedEvents.StreamOf(r)["timeOffsetSeconds"] = null)),
            ["timeOffsetSeconds-element-null"] = (Malformed, () => Mutated(r => ImportedEvents.StreamOf(r)["timeOffsetSeconds"]!.AsArray()[3] = null)),
            ["laps-null"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["laps"] = null)),
            ["laps-missing"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r).Remove("laps"))),
            ["laps-null-element"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["laps"]!.AsArray()[1] = null)),
            ["summary-null"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["summary"] = null)),
            ["startTime-missing"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r).Remove("startTime"))),

            // Sport: sconosciuto come testo = JSON non deserializzabile; numero non definito = valore fuori dall'enum.
            ["sport-unknown-text"] = (Malformed, () => Mutated(r => ImportedEvents.DataOf(r)["sport"] = "kayak")),
            ["sport-undefined-number"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["sport"] = 9)),
            ["sport-null"] = (Malformed, () => Mutated(r => ImportedEvents.DataOf(r)["sport"] = null)),

            // Chiavi di deduplicazione e utente.
            ["externalId-empty"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["externalId"] = string.Empty)),
            ["externalId-blank"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["externalId"] = "   ")),
            ["externalId-null"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["externalId"] = null)),
            ["externalId-nul-char"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["externalId"] = "abc\u0000def")),
            ["externalId-too-long"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["externalId"] = new string('x', 257))),
            ["source-empty"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["source"] = string.Empty)),
            ["source-null"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["source"] = null)),
            ["userId-empty"] = (Validation, () => Mutated(r => r["userId"] = Guid.Empty.ToString())),
            ["version-2"] = (Validation, () => Mutated(r => r["version"] = 2)),
            ["type-other"] = (Validation, () => Mutated(r => r["type"] = "SomethingElse")),

            // Numeri: NaN come testo non e' un numero JSON (Malformed). Un letterale che trabocca (1e999) invece STJ lo legge
            // come infinito: arriva al gestore, che lo rifiuta (Validation). Idem un valore finito fuori intervallo.
            ["elapsedSeconds-1e999"] = (Validation, () => Mutated(r => ImportedEvents.DataOf(r)["elapsedSeconds"] = JsonNode.Parse("1e999"))),
            ["heartRate-element-1e999"] = (Validation, () => Mutated(r => ImportedEvents.StreamOf(r)["heartRate"]!.AsArray()[5] = JsonNode.Parse("1e999"))),
            ["timeOffsetSeconds-element-1e999"] = (Validation, () => Mutated(r => ImportedEvents.StreamOf(r)["timeOffsetSeconds"]!.AsArray()[5] = JsonNode.Parse("1e999"))),
            ["elapsedSeconds-NaN-text"] = (Malformed, () => Mutated(r => ImportedEvents.DataOf(r)["elapsedSeconds"] = "NaN")),
            ["timeOffsetSeconds-out-of-range"] = (Validation, () => Mutated(r => ImportedEvents.StreamOf(r)["timeOffsetSeconds"]!.AsArray()[39] = 1e12)),
        };

        foreach (var column in ImportedEvents.ValueColumns)
        {
            cases[$"{column}-shorter"] = (Validation, () => Mutated(r => ImportedEvents.DropLast(r, column)));
        }

        // Troppi punti: 100.001 (solo tempo e frequenza cardiaca, per tenere l'evento piccolo).
        cases["points-100001"] = (Validation, () =>
        {
            var offsets = Enumerable.Range(0, StreamLimits.MaxPoints + 1).Select(i => (double)i).ToArray();
            var stream = new StreamColumns(offsets, null, null, null, Enumerable.Repeat<double?>(120, offsets.Length).ToArray(), null, null, null);
            return ImportedEvents.Serialize(ImportedEvents.Envelope(ImportedEvents.Data("file", "h", Sport.Run, 1, 1) with
            {
                ExternalId = $"hash-{Guid.NewGuid():N}",
                Stream = stream,
            }));
        });
        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidCaseNames))]
    public async Task InvalidEvent_GoesStraightToDlq_WithoutRetry_WritesNothing_AndTheNextValidEventIsProcessed(string caseName)
    {
        var (expectedReason, buildPayload) = InvalidCases[caseName];
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        var payload = buildPayload();
        var baseline = await s.CountsAsync();

        await s.PublishRawAsync(payload);
        await Eventually.AssertAsync(async () => await s.DlqCountAsync() >= 1, Timeout, "evento non valido in DLQ");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio terminato");

        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal(expectedReason, dlq.Headers["Dlq-Reason"]);
        Assert.Equal("1", dlq.Headers["Dlq-Deliveries"]);   // una sola consegna: nessun ritentativo
        Assert.Equal(ImportScenario.Durable, dlq.Headers["Dlq-Consumer"]);
        Assert.Equal(EventNames.ActivityImportedSubject, dlq.Headers["Dlq-Original-Subject"]);
        Assert.Equal(payload, dlq.Data);
        Assert.Equal(1, counters.DeadLettered);
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.Redelivered);
        Assert.Equal(0, s.Probe.ActivityInsertCount);   // il gestore non e' arrivato al database
        Assert.Equal(baseline, await s.CountsAsync());   // nessuna riga in activities, activity_laps, activity_stream, inbox

        // AC-012: dopo una DLQ la coda non e' bloccata.
        var next = ImportedEvents.Valid(points: 12, laps: 1);
        await s.PublishAsync(next);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento valido successivo elaborato");
        var activity = Assert.Single(await s.ActivitiesAsync(next.UserId));
        Assert.Equal(12, await s.PointCountAsync(activity.Id));
        Assert.Equal(1, await s.DlqCountAsync());
    }

    // ---- Atomicita' e guasti ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailureAfterTheActivityInsert_RollsBackEverything_AndTheRetrySavesOneActivity()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        s.Probe.FailLapInserts(1);   // l'attivita' e' gia' scritta nella transazione quando l'INSERT dei giri fallisce
        var envelope = ImportedEvents.Valid(points: 30, laps: 3);

        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato al secondo tentativo");

        Assert.Equal(1, counters.Retried);
        Assert.Equal(1, counters.Redelivered);
        Assert.Equal(2, s.Probe.ActivityInsertCount);   // il primo tentativo ha inserito e poi e' stato annullato per intero
        Assert.Single(s.Probe.Failures);
        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        Assert.Equal(3, (await s.LapsAsync(activity.Id)).Count);
        Assert.Equal(30, await s.PointCountAsync(activity.Id));   // non 60: i punti del primo tentativo non sono rimasti
        Assert.True(await s.InboxContainsAsync(envelope.Id));
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task FailureAtCommit_AfterActivityLapsPointsAndInbox_RollsBackAll_AndTheRetrySavesOneActivity()
    {
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(FastRetries);
        var counters = ImportScenario.CountersOf(host);
        s.Probe.FailCommits(1);   // tutto e' gia' scritto nella transazione (COPY compreso): il commit fallisce
        var envelope = ImportedEvents.Valid(points: 200, laps: 2);

        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento elaborato al secondo tentativo");

        Assert.Equal(1, counters.Retried);
        var activity = Assert.Single(await s.ActivitiesAsync(envelope.UserId));
        Assert.Equal(2, (await s.LapsAsync(activity.Id)).Count);
        Assert.Equal(200, await s.PointCountAsync(activity.Id));
        Assert.Equal(1, await CountInboxRowsAsync(s, envelope.Id));
    }

    [Theory]
    [InlineData("laps")]
    [InlineData("commit")]
    [InlineData("activity")]
    public async Task PermanentFailure_RetriesUpToMaxDeliver_ThenDlq_WithNothingLeftBehind(string where)
    {
        await using var s = await NewScenarioAsync();
        var settings = new Dictionary<string, string?>(FastRetries) { ["Consumers:ActivityImported:MaxDeliver"] = "3" };
        await using var host = s.StartHost(settings);
        var counters = ImportScenario.CountersOf(host);
        var baseline = await s.CountsAsync();
        switch (where)
        {
            case "laps": s.Probe.FailLapInserts(DatabaseProbe.Always); break;
            case "commit": s.Probe.FailCommits(DatabaseProbe.Always); break;
            default: s.Probe.FailActivityInserts(DatabaseProbe.Always); break;
        }

        var envelope = ImportedEvents.Valid(points: 100, laps: 2);
        await s.PublishAsync(envelope);
        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1, Timeout, "evento in DLQ dopo MaxDeliver tentativi");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio terminato");

        var dlq = Assert.Single(await s.DlqAsync());
        // Solo il tipo dell'eccezione, mai il testo: SaveChanges avvolge l'errore dei giri in DbUpdateException.
        Assert.Equal(where == "laps" ? "DbUpdateException" : "InvalidOperationException", dlq.Headers["Dlq-Reason"]);
        Assert.Equal("3", dlq.Headers["Dlq-Deliveries"]);
        Assert.Equal(3, s.Probe.Failures.Count);
        Assert.Equal(2, counters.Retried);
        Assert.Equal(baseline, await s.CountsAsync());   // rollback completo: nemmeno l'attivita', i giri, i punti o l'inbox
        Assert.Empty(await s.ActivitiesAsync(envelope.UserId));
    }

    [Fact]
    public async Task AfterAPermanentlyFailingEventGoesToDlq_TheNextEventIsProcessed()
    {
        await using var s = await NewScenarioAsync();
        var settings = new Dictionary<string, string?>(FastRetries) { ["Consumers:ActivityImported:MaxDeliver"] = "2" };
        await using var host = s.StartHost(settings);
        var counters = ImportScenario.CountersOf(host);
        s.Probe.FailLapInserts(2);   // esattamente i due tentativi del primo evento
        var failing = ImportedEvents.Valid(points: 10, laps: 2);
        var next = ImportedEvents.Valid(points: 15, laps: 2);

        await s.PublishAsync(failing);
        await s.PublishAsync(next);

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1 && counters.Processed >= 1, Timeout, "primo in DLQ e secondo elaborato");
        Assert.Empty(await s.ActivitiesAsync(failing.UserId));
        var activity = Assert.Single(await s.ActivitiesAsync(next.UserId));
        Assert.Equal(15, await s.PointCountAsync(activity.Id));
    }

    // ---- Privacy (REQ-034) ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logs_NeverContainEventContent_ForAValidEvent_ADlqEvent_AndARetriedEvent()
    {
        // Marcatori NON numerici o con confini espliciti: nei log compaiono GUID, TraceId e sequenze che contengono cifre.
        const string marker = "zqxprivacymarker";
        const double latitude = 47.3769123;
        const double longitude = 8.5417456;
        var logs = new LogCapture();
        await using var s = await NewScenarioAsync();
        await using var host = s.StartHost(
            new Dictionary<string, string?>(FastRetries)
            {
                // Tutti i livelli, anche i log dei comandi di EF Core (di norma a Warning): nemmeno cosi' devono comparire dati.
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
            },
            logs: logs);
        var counters = ImportScenario.CountersOf(host);

        EventEnvelope<ActivityImported> Sensitive(string kind, int points = 20)
        {
            var count = points;
            var stream = StreamTestData.Build(count, allColumns: true) with
            {
                Latitude = Enumerable.Repeat<double?>(latitude, count).ToArray(),
                Longitude = Enumerable.Repeat<double?>(longitude, count).ToArray(),
                // Decimali con molte cifre: non possono comparire per caso (un PID, una sequenza di stream) in un log.
                HeartRate = Enumerable.Repeat<double?>(233.5, count).ToArray(),
                PowerWatts = Enumerable.Repeat<double?>(4739.25, count).ToArray(),
            };
            return ImportedEvents.Envelope(ImportedEvents.Data($"{marker}src", $"{marker}ext{kind}", Sport.Ride, count, 2) with { Stream = stream });
        }

        // 1) valido; 2) elaborato al secondo tentativo (guasto iniettato con testo privo di dati); 3) in DLQ per validazione.
        var valid = Sensitive("valid");
        await s.PublishAsync(valid);
        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "evento valido elaborato");

        s.Probe.FailLapInserts(1);
        await s.PublishAsync(Sensitive("retry"));
        await Eventually.AssertAsync(() => counters.Processed >= 2 && counters.Retried >= 1, Timeout, "evento con retry elaborato");

        var invalid = ImportedEvents.Mutate(Sensitive("invalid"), r => ImportedEvents.DropLast(r, "latitude"));
        await s.PublishRawAsync(invalid);
        await Eventually.AssertAsync(() => counters.DeadLettered >= 1, Timeout, "evento non valido in DLQ");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "tutto confermato");

        var entries = logs.Entries.ToArray();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Text.Contains("DLQ", StringComparison.Ordinal));      // il test non e' vuoto
        Assert.Contains(entries, e => e.Text.Contains("INSERT INTO", StringComparison.Ordinal));   // log dei comandi di EF attivi
        foreach (var (category, _, text) in entries)
        {
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"Il log di {category} contiene externalId o source: {text}");
            Assert.False(text.Contains("47.3769123", StringComparison.Ordinal), $"Il log di {category} contiene la latitudine: {text}");
            Assert.False(text.Contains("8.5417456", StringComparison.Ordinal), $"Il log di {category} contiene la longitudine: {text}");
            Assert.False(text.Contains("233.5", StringComparison.Ordinal), $"Il log di {category} contiene la frequenza cardiaca: {text}");
            Assert.False(text.Contains("4739.25", StringComparison.Ordinal), $"Il log di {category} contiene la potenza: {text}");
        }
    }

    private static async Task<long> CountInboxRowsAsync(ImportScenario scenario, Guid eventId) =>
        await scenario.InboxContainsAsync(eventId) ? 1 : 0;
}
