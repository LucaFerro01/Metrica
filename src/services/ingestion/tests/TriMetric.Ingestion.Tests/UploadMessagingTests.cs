using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>Dall'upload allo stream reale <c>EVENTS</c> tramite outbox e relay, anche con NATS fermo (REQ-012, REQ-013, AC-005).</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadMessagingTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static async Task WaitUntilPublishedAsync(TimescaleFixture db, Guid userId, int expectedRows)
    {
        await Eventually.AssertAsync(async () =>
        {
            var rows = await UploadDb.OutboxAsync(db, userId);
            return rows.Count == expectedRows && rows.All(r => r.PublishedAt is not null);
        }, Patience, "il relay deve marcare come pubblicate le righe dell'outbox");
    }

    [Fact]
    public async Task EndToEnd_AValidUpload_AppearsOnTheEventsStream_AndDeserializes()
    {
        await using var app = UploadApp.Start(db, nats);
        var content = UploadSamples.Run();
        var correlation = Guid.NewGuid();

        var reply = await app.UploadAsync("e2e.gpx", content, correlationId: correlation.ToString());

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        await WaitUntilPublishedAsync(db, app.UserId, 1);
        var row = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
        var js = app.Services.GetRequiredService<INatsJSContext>();
        var sequence = Assert.Single(await EventsStream.SequencesOfAsync(js, EventNames.ActivityImportedSubject, row.Id));
        var envelope = await EventsStream.ReadAsync(js, EventNames.ActivityImportedSubject, sequence);
        Assert.Equal(row.Id, envelope.Id);
        Assert.Equal(EventNames.ActivityImported, envelope.Type);
        Assert.Equal(1, envelope.Version);
        Assert.Equal(app.UserId, envelope.UserId);
        Assert.Equal(correlation, envelope.CorrelationId);
        Assert.Equal(Sport.Run, envelope.Data.Sport);
        Assert.Equal(UploadSamples.Sha256(content), envelope.Data.ExternalId);
        Assert.Equal(reply.UploadId, (await UploadDb.UploadsAsync(db, app.UserId)).Single().Id);
    }

    [Fact]
    public async Task NatsDown_TheUploadIsStillAccepted_AndTheEventIsPublishedOnceWhenNatsReturns()
    {
        await nats.StopAsync();
        try
        {
            await using var app = UploadApp.Start(db, nats);
            var content = UploadSamples.Run();
            var reply = await app.UploadAsync("senza-nats.gpx", content);

            Assert.Equal(HttpStatusCode.Accepted, reply.Status);
            var upload = Assert.Single(await UploadDb.UploadsAsync(db, app.UserId));
            var pending = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
            Assert.Equal(reply.UploadId, upload.Id);
            Assert.Null(pending.PublishedAt);

            // Con NATS ancora giu': lo stesso file e' un duplicato (nessun secondo evento), un file diverso si accoda.
            var duplicate = await app.UploadAsync("senza-nats.gpx", content);
            Assert.Equal(HttpStatusCode.OK, duplicate.Status);
            Assert.Equal("duplicate", duplicate.ResponseStatus);
            var other = await app.UploadAsync("altro.gpx", UploadSamples.Run());
            Assert.Equal(HttpStatusCode.Accepted, other.Status);

            await nats.StartAsync();
            await WaitUntilPublishedAsync(db, app.UserId, 2);

            var js = app.Services.GetRequiredService<INatsJSContext>();
            foreach (var row in await UploadDb.OutboxAsync(db, app.UserId))
            {
                Assert.Single(await EventsStream.SequencesOfAsync(js, EventNames.ActivityImportedSubject, row.Id));
            }
        }
        finally
        {
            await nats.EnsureRunningAsync();
        }
    }
}
