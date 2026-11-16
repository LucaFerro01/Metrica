using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TriMetric.Contracts;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>AC-012 (REQ-017): messaggi non elaborabili in DLQ con le intestazioni <c>Dlq-*</c>, senza bloccare gli eventi validi.</summary>
[Collection(E2eCollection.Name)]
public sealed class DeadLetterTests(TimescaleFixture db, NatsFixture nats)
{
    private const string Subject = EventNames.ActivityImportedSubject;

    [Fact]
    public async Task Ac012_UnprocessableMessages_GoToTheDlq_AndLaterValidEventsAreProcessed()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var marker = $"riservato-{Guid.NewGuid():N}";
        var good = EventFactory.Valid(system.UserId, start: new DateTimeOffset(2024, 6, 2, 6, 0, 0, TimeSpan.Zero));

        // Quattro modi di non essere elaborabile, pubblicati sul subject REALE, tutti PRIMA degli eventi validi.
        var bad = new Dictionary<string, byte[]>
        {
            // Non e' JSON.
            ["Malformed:not-json"] = Encoding.UTF8.GetBytes($"{{ non e' json {marker} "),
            // JSON ma senza busta (nessun id).
            ["Malformed:no-envelope"] = Encoding.UTF8.GetBytes($"{{\"nota\":\"{marker}\"}}"),
            // Busta valida con colonne di lunghezza diversa: errore di validazione, nessun retry.
            ["Validation:columns"] = EventFactory.Mutate(
                EventFactory.Valid(system.UserId, externalId: $"hash-{marker}"),
                root => root["data"]!["stream"]!["heartRate"]!.AsArray().RemoveAt(0)),
            // Versione del contratto sconosciuta.
            ["Validation:version"] = EventFactory.Mutate(
                EventFactory.Valid(system.UserId, externalId: $"hash-{marker}-v"),
                root => root["version"] = 99),
        };
        system.Protect(new PrivacyProbes().AddText("payload non elaborabile", marker).AddEventBody());

        foreach (var payload in bad.Values)
        {
            await system.NatsProbe.PublishEventAsync(payload);
        }

        // Dal VERO upload (gateway -> ingestion -> outbox -> NATS) e da un evento costruito a mano, entrambi dopo quelli difettosi.
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 80, Start = new DateTimeOffset(2024, 6, 3, 6, 0, 0, TimeSpan.Zero) }.Build();
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(file));
        await system.NatsProbe.PublishEventAsync(good);

        await system.Api.WaitForActivityAsync(file.Sha256);
        await system.Api.WaitForActivityAsync(good.Data.ExternalId);
        await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore non ha messaggi in sospeso");

        // DLQ sul server: una voce per ogni messaggio difettoso, con il payload intatto e le intestazioni attese.
        // Si attende sul conteggio (informazioni dello stream) e i messaggi si leggono UNA volta: ogni lettura crea un consumatore.
        await Eventually.ValueAsync(
            () => system.NatsProbe.DlqCountAsync(),
            count => count >= bad.Count,
            Eventually.Normal,
            $"la DLQ contiene {bad.Count} messaggi",
            count => $"{count} messaggi");
        var dlq = await system.NatsProbe.DlqAsync();
        Assert.Equal(bad.Count, dlq.Count);
        foreach (var (label, payload) in bad)
        {
            var entry = Assert.Single(dlq, e => e.Data.SequenceEqual(payload));
            Assert.Equal(label.Split(':')[0], entry.Header("Dlq-Reason"));
            Assert.Equal(Subject, entry.Header("Dlq-Original-Subject"));
            Assert.Equal("1", entry.Header("Dlq-Deliveries"));   // nessun retry per un messaggio non valido
            Assert.Equal(NatsProbe.ActivitiesDurable, entry.Header("Dlq-Consumer"));
            Assert.StartsWith($"{NatsProbe.ActivitiesDurable}:", entry.Header("Nats-Msg-Id"));
            Assert.Equal(NatsProbe.DlqSubject, entry.Subject);
        }

        // Il consumatore non si e' bloccato: gli eventi validi sono in elenco e nulla e' rimasto in sospeso.
        var list = await system.Api.ListPageAsync();
        Assert.Equal(2, list.Total);
        Assert.Equal(bad.Count, system.ConsumerCounters.DeadLettered);
        Assert.Equal(2, system.ConsumerCounters.Processed);
        Assert.Equal(2L, await system.Db.ActivityCountAsync());
        Assert.Equal(bad.Count + 2, await system.NatsProbe.EventsCountAsync());

        system.AssertLogsContainNoPersonalData();
    }

    /// <summary>
    /// Un evento valido per il contratto ma che il database rifiuta a OGNI tentativo (un vincolo violato solo da quel messaggio): ritentato
    /// con attesa crescente e, finiti i tentativi, in DLQ; gli eventi successivi sono elaborati. Con <c>MaxDeliver</c> 3 e attese di
    /// 200 e 400 ms il test dura pochi secondi.
    /// </summary>
    [Fact]
    public async Task Ac012_AnEventThatFailsOnEveryAttempt_IsRetried_ThenDeadLettered_AndLaterEventsAreProcessed()
    {
        const double ForbiddenLapDistance = 424242.4242;
        var options = new SystemOptions
        {
            Activities = new Dictionary<string, string>
            {
                ["Consumers:ActivityImported:MaxDeliver"] = "3",
                ["Consumers:ActivityImported:BackoffMilliseconds:0"] = "200",
                ["Consumers:ActivityImported:BackoffMilliseconds:1"] = "400",
            },
        };
        await using var system = await EndToEndSystem.StartAsync(db, nats, options);
        var poisoned = EventFactory.Valid(
            system.UserId,
            externalId: $"hash-veleno-{Guid.NewGuid():N}",
            laps: [new LapData(new DateTimeOffset(2024, 6, 1, 6, 0, 0, TimeSpan.Zero), 10, ForbiddenLapDistance)]);
        var good = EventFactory.Valid(system.UserId, start: new DateTimeOffset(2024, 6, 5, 6, 0, 0, TimeSpan.Zero));
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 70, Start = new DateTimeOffset(2024, 6, 6, 6, 0, 0, TimeSpan.Zero) }.Build();

        // Il vincolo e' del database dello scenario (si toglie sempre alla fine): il solo giro con questa distanza viola il CHECK.
        await system.Db.ExecuteAsync(
            $"ALTER TABLE activities.activity_laps ADD CONSTRAINT it_forbidden_lap CHECK (distance_meters <> {ForbiddenLapDistance.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        try
        {
            await system.NatsProbe.PublishEventAsync(poisoned);
            await system.NatsProbe.PublishEventAsync(good);
            Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(file));

            await system.Api.WaitForActivityAsync(good.Data.ExternalId);
            await system.Api.WaitForActivityAsync(file.Sha256);
            await Eventually.ValueAsync(
                () => system.NatsProbe.DlqCountAsync(),
                count => count >= 1,
                Eventually.Normal,
                "il messaggio che fallisce sempre arriva in DLQ",
                count => $"{count} messaggi");
            var dlq = await system.NatsProbe.DlqAsync();
            await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore non ha messaggi in sospeso");

            var entry = Assert.Single(dlq);
            Assert.Equal("3", entry.Header("Dlq-Deliveries"));   // tutti i tentativi consentiti
            Assert.Equal(Subject, entry.Header("Dlq-Original-Subject"));
            Assert.Equal(NatsProbe.ActivitiesDurable, entry.Header("Dlq-Consumer"));
            var reason = entry.Header("Dlq-Reason");
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.NotEqual("Validation", reason);   // l'evento e' valido: a fallire e' il salvataggio
            Assert.NotEqual("Malformed", reason);
            Assert.Equal(poisoned.Id, entry.Envelope.Id);
            Assert.Equal(2, system.ConsumerCounters.Retried);   // tentativi 1 e 2 con Nak e attesa, il terzo va in DLQ
            Assert.Equal(1, system.ConsumerCounters.DeadLettered);

            // Nessuna traccia dell'evento difettoso nel database; gli altri sono tutti salvati.
            Assert.DoesNotContain(await system.Db.ActivitiesOfAsync(system.UserId), a => a.ExternalId == poisoned.Data.ExternalId);
            Assert.Equal(2, (await system.Api.ListPageAsync()).Total);
            Assert.Equal(2L, await system.Db.InboxCountAsync());
            system.AssertLogsContainNoPersonalData();
        }
        finally
        {
            await system.Db.ExecuteAsync("ALTER TABLE activities.activity_laps DROP CONSTRAINT IF EXISTS it_forbidden_lap");
        }
    }
}
