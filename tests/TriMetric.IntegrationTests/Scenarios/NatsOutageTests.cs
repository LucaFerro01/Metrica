using System.Net;
using TriMetric.IntegrationTests.Files;
using TriMetric.IntegrationTests.Support;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Scenarios;

/// <summary>AC-005 (REQ-013): con NATS fermo il caricamento riesce; riavviato (stessa porta) l'attivita' compare senza doppioni.</summary>
[Collection(E2eCollection.Name)]
public sealed class NatsOutageTests(TimescaleFixture db, NatsFixture nats)
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task Ac005_UploadsWhileNatsIsDown_AreAccepted_AndAppearOnceAfterTheRestart(int uploads)
    {
        // Gli host sono gia' connessi a NATS quando parte il test (EndToEndSystem.StartAsync lo attende).
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var files = Enumerable.Range(0, uploads)
            .Select(i => new GpxTrack
            {
                Seed = Guid.NewGuid(),
                Points = 60 + i * 10,
                Start = new DateTimeOffset(2024, 5, 10 + i, 6, 0, 0, TimeSpan.Zero),
            }.Build())
            .ToList();

        try
        {
            await nats.StopAsync();

            foreach (var file in files)
            {
                var response = await system.UploadAsync(file);
                Expect.Status(HttpStatusCode.Accepted, response);
                Assert.Equal("accepted", response.UploadStatus);
            }

            // Durante il fermo: il caricamento e' registrato con l'evento in outbox, non ancora pubblicato; nessuna attivita'.
            Assert.Equal((long)uploads, await system.Db.UploadCountAsync(system.UserId));
            var pending = await system.Db.OutboxAsync();
            Assert.Equal(uploads, pending.Count);
            Assert.All(pending, row => Assert.Null(row.PublishedAt));
            Assert.Equal(0, (await system.Api.ListPageAsync()).Total);

            // NATS resta fermo finche il relay NON HA DAVVERO FALLITO almeno una pubblicazione (il suo timeout e' di 10 s): senza questa
            // attesa il riavvio potrebbe arrivare mentre il client ancora attende la riconnessione e il percorso dell'errore non girerebbe.
            await Eventually.AssertAsync(
                () => Task.FromResult(system.RelayStats.Failures >= 1),
                Eventually.Long,
                "il relay ha registrato una pubblicazione fallita mentre NATS e' fermo");
            Assert.Contains(await system.Db.OutboxAsync(), row => row.Attempts >= 1);
            Assert.All(await system.Db.OutboxAsync(), row => Assert.Null(row.PublishedAt));

            await nats.StartAsync();

            await system.Api.WaitForTotalAsync(uploads);
            await Eventually.AssertAsync(
                async () => (await system.Db.OutboxAsync()).All(r => r.PublishedAt is not null),
                Eventually.Long,
                "tutta l'outbox e' pubblicata dopo il riavvio di NATS");
            Assert.True(
                await Eventually.ValueAsync(
                    () => system.NatsProbe.DurableDrainedAsync(), drained => drained, Eventually.Long, "il consumatore ha elaborato tutto"));

            // Senza doppioni: un'attivita' per file e UN solo messaggio per file, contati dal server NATS.
            var list = await system.Api.ListPageAsync();
            Assert.Equal(uploads, list.Total);
            Assert.Equal(files.Select(f => f.Sha256).Order(), list.Items.Select(i => i.ExternalId).Order());
            Assert.Equal(uploads, await system.NatsProbe.EventsCountAsync());

            var published = await system.NatsProbe.EventsAsync();
            Assert.Equal(uploads, published.Count);
            Assert.Equal(uploads, published.Select(m => m.Envelope.Id).Distinct().Count());
            Assert.Equal(files.Select(f => f.Sha256).Order(), published.Select(m => m.Envelope.Data.ExternalId).Order());
            Assert.Equal((await system.Db.OutboxAsync()).Select(r => r.Id).Order(), published.Select(m => m.Envelope.Id).Order());
            Assert.Equal(published.Select(m => m.Envelope.Id.ToString()).Order(), published.Select(m => m.Header("Nats-Msg-Id")).Order());

            Assert.Equal((long)uploads, await system.Db.ActivityCountAsync());
            Assert.Equal((long)uploads, await system.Db.InboxCountAsync());
            Assert.Equal(uploads, system.ConsumerCounters.Processed);
            Assert.Equal(0, system.ConsumerCounters.DeadLettered);
            Assert.Equal(0, await system.NatsProbe.DlqCountAsync());

            system.AssertLogsContainNoPersonalData();
        }
        finally
        {
            // Qualunque cosa accada, gli altri test trovano NATS acceso.
            await nats.EnsureRunningAsync();
        }
    }
}
