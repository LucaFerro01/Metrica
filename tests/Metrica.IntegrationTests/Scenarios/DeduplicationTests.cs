using System.Net;
using Metrica.IntegrationTests.Files;
using Metrica.IntegrationTests.Support;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Scenarios;

/// <summary>AC-003 (REQ-011): lo stesso file caricato due volte non crea una seconda attivita' ne' un secondo evento.</summary>
[Collection(E2eCollection.Name)]
public sealed class DeduplicationTests(TimescaleFixture db, NatsFixture nats)
{
    [Fact]
    public async Task Ac003_SameFileTwice_IsADuplicate_OneActivity_OneEventPublished()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var file = new GpxTrack { Seed = Guid.NewGuid(), Points = 120, Start = new DateTimeOffset(2024, 5, 3, 7, 0, 0, TimeSpan.Zero) }.Build();

        var first = await system.UploadAsync(file);
        Expect.Status(HttpStatusCode.Accepted, first);
        Assert.Equal("accepted", first.UploadStatus);
        await system.Api.WaitForActivityAsync(file.Sha256);

        var second = await system.UploadAsync(file);
        Expect.Status(HttpStatusCode.OK, second);
        Assert.Equal("duplicate", second.UploadStatus);
        Assert.Equal(first.UploadId, second.UploadId);
        Assert.Equal(file.Sha256, second.ExternalId);

        // Stesso contenuto con un altro nome: la deduplicazione e' sul contenuto, non sul nome (REQ-011).
        var renamed = await system.UploadAsync(new GeneratedFile($"copia-{Guid.NewGuid():N}.gpx", file.Content, file.Probes));
        Expect.Status(HttpStatusCode.OK, renamed);
        Assert.Equal("duplicate", renamed.UploadStatus);
        Assert.Equal(first.UploadId, renamed.UploadId);

        // Un eventuale secondo evento passerebbe dall'outbox: si attende che sia vuota (tutto pubblicato) prima di contare.
        await Eventually.AssertAsync(
            async () => (await system.Db.OutboxAsync()).All(r => r.PublishedAt is not null),
            Eventually.Normal,
            "l'outbox di ingestion e' tutta pubblicata");

        var list = await system.Api.ListPageAsync();
        Assert.Equal(1, list.Total);
        Assert.Single(list.Items);

        // Un solo evento, contato dal server NATS (non dal codice sotto test) e con la busta attesa.
        Assert.Equal(1, await system.NatsProbe.EventsCountAsync());
        var published = Assert.Single(await system.NatsProbe.EventsAsync());
        var outbox = Assert.Single(await system.Db.OutboxAsync());
        Assert.Equal(outbox.Id.ToString(), published.Header("Nats-Msg-Id"));
        Assert.Equal(outbox.Id, published.Envelope.Id);
        Assert.Equal(system.UserId, published.Envelope.UserId);
        Assert.Equal(file.Sha256, published.Envelope.Data.ExternalId);
        Assert.Equal("file", published.Envelope.Data.Source);

        Assert.Equal(1L, await system.Db.UploadCountAsync(system.UserId));
        Assert.Equal(1L, await system.Db.ActivityCountAsync());
        Assert.Equal(1L, await system.Db.InboxCountAsync());

        // Il consumatore ha ricevuto e salvato un solo evento.
        Assert.Equal(1, system.ConsumerCounters.Processed);
        Assert.Equal(0, system.ConsumerCounters.Duplicates);
        Assert.Equal(0, await system.NatsProbe.DlqCountAsync());

        system.AssertLogsContainNoPersonalData();
    }
}
