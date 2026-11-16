using System.Net;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>AC-006 (REQ-015): lo stesso evento (stesso <c>id</c>) consegnato piu' volte produce una sola attivita' e nessun errore.</summary>
[Collection(E2eCollection.Name)]
public sealed class RedeliveryTests(TimescaleFixture db, NatsFixture nats)
{
    [Fact]
    public async Task Ac006_AnAlreadyProcessedEvent_DeliveredAgain_CreatesNoSecondActivity_NoErrors_EmptyDlq()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 90, Start = new DateTimeOffset(2024, 5, 3, 7, 0, 0, TimeSpan.Zero) }.Build();
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(file));
        await system.Api.WaitForActivityAsync(file.Sha256);
        await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore ha confermato il primo evento");

        // Il messaggio vero pubblicato da ingestion, letto dal server e RIPUBBLICATO senza Nats-Msg-Id: due messaggi distinti
        // dello stream con la stessa busta (stesso Envelope.Id), come una riconsegna dopo un ack perso.
        var original = Assert.Single(await system.NatsProbe.EventsAsync());
        var logMark = system.Logs.Count;
        var receivedBefore = system.ConsumerCounters.Received;
        await system.NatsProbe.PublishEventAsync(original.Data);
        Assert.Equal(2, await system.NatsProbe.EventsCountAsync());

        await Eventually.AssertAsync(
            () => Task.FromResult(system.ConsumerCounters.Received >= receivedBefore + 1 && system.ConsumerCounters.Duplicates >= 1),
            Eventually.Normal,
            "il consumatore ha ricevuto la seconda copia e l'ha riconosciuta come duplicato");
        await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore ha confermato anche la copia");

        Assert.Equal(1, system.ConsumerCounters.Processed);
        Assert.Equal(1, system.ConsumerCounters.Duplicates);
        Assert.Equal(1, (await system.Api.ListPageAsync()).Total);
        Assert.Equal(1L, await system.Db.ActivityCountAsync());
        Assert.Equal(1L, await system.Db.InboxCountAsync());
        Assert.Equal(0, await system.NatsProbe.DlqCountAsync());
        system.AssertNoErrorsSince(logMark);
        system.AssertLogsContainNoPersonalData();
    }

    [Fact]
    public async Task Ac006_TwoMessagesWithTheSameEnvelopeId_CreateOneActivity()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var envelope = EventFactory.Valid(system.UserId, externalId: $"hash-{Guid.NewGuid():N}");
        var payload = EventFactory.Serialize(envelope);
        var logMark = system.Logs.Count;

        // Due messaggi distinti nello stream (nessun Nats-Msg-Id: il server non deduplica) con la stessa busta.
        await system.NatsProbe.PublishEventAsync(payload);
        await system.NatsProbe.PublishEventAsync(payload);
        Assert.Equal(2, await system.NatsProbe.EventsCountAsync());

        await Eventually.AssertAsync(
            () => Task.FromResult(system.ConsumerCounters.Processed + system.ConsumerCounters.Duplicates >= 2),
            Eventually.Normal,
            "il consumatore ha gestito entrambi i messaggi");
        await Eventually.AssertAsync(() => system.NatsProbe.DurableDrainedAsync(), Eventually.Normal, "il consumatore ha confermato tutto");

        Assert.Equal(1, system.ConsumerCounters.Processed);
        Assert.Equal(1, system.ConsumerCounters.Duplicates);
        var list = await system.Api.ListPageAsync();
        Assert.Equal(1, list.Total);
        Assert.Equal(envelope.Data.ExternalId, list.Items[0].ExternalId);
        Assert.Equal(1L, await system.Db.InboxCountAsync());
        Assert.Equal(0, await system.NatsProbe.DlqCountAsync());
        system.AssertNoErrorsSince(logMark);
    }
}
