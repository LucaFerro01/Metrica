using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;
using TriMetric.Messaging;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// Cablaggio dell'outbox nell'host reale (REQ-012): senza <c>AddOutbox</c> <c>IOutbox</c> non e' registrato e T10 fallirebbe a
/// runtime; senza <c>AddOutboxRelay</c> gli eventi non partirebbero mai.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Persistence")]
public sealed class OutboxWiringTests(TimescaleFixture db, NatsFixture nats)
{
    private sealed record WiringPayload(string Name);

    [Fact]
    public async Task HostWritesAnEventToTheOutbox_AndTheRelayPublishesItToJetStream()
    {
        await using var host = IngestionHost.Development(db, nats);
        using var client = host.Start();
        var subject = $"ingestion.t09.{Guid.NewGuid():N}";
        var envelope = new EventEnvelope<WiringPayload>(
            Guid.NewGuid(), EventNames.ActivityImported, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(),
            new WiringPayload("cablaggio"));

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();   // registrato da AddOutbox<IngestionDbContext>()
            outbox.Add(subject, envelope);
            await scope.ServiceProvider.GetRequiredService<IngestionDbContext>().SaveChangesAsync();
        }

        await Eventually.AssertAsync(async () =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            var row = await scope.ServiceProvider.GetRequiredService<IngestionDbContext>().Outbox
                .AsNoTracking().SingleAsync(m => m.Id == envelope.Id);
            return row.PublishedAt is not null;
        }, TimeSpan.FromSeconds(20), "il relay deve marcare come pubblicata la riga dell'outbox");

        var js = host.Services.GetRequiredService<INatsJSContext>();
        var messages = await StreamMessagesAsync(js, subject);
        var stored = Assert.Single(messages);
        Assert.Equal(envelope.Id.ToString(), stored.MsgId);
        Assert.Equal(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options), stored.Data);
    }

    [Fact]
    public async Task OutboxRow_AndDomainRow_AreWrittenInTheSameTransaction()
    {
        await using var host = IngestionHost.Development(db, nats);
        host.Start();
        var envelope = new EventEnvelope<WiringPayload>(
            Guid.NewGuid(), EventNames.ActivityImported, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(),
            new WiringPayload("transazione"));
        var userId = Guid.NewGuid();
        var externalId = Guid.NewGuid().ToString("N");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            context.Uploads.Add(new Upload
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Source = "file",
                ExternalId = externalId,
                FileName = "corsa.gpx",
                SizeBytes = 123,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            scope.ServiceProvider.GetRequiredService<IOutbox>().Add($"ingestion.t09.{Guid.NewGuid():N}", envelope);
            await context.SaveChangesAsync();
        }

        await using var verify = host.Services.CreateAsyncScope();
        var check = verify.ServiceProvider.GetRequiredService<IngestionDbContext>();
        Assert.Equal(1, await check.Uploads.CountAsync(u => u.UserId == userId && u.ExternalId == externalId));
        Assert.Equal(1, await check.Outbox.CountAsync(m => m.Id == envelope.Id));
    }

    private sealed record Stored(string? MsgId, byte[] Data);

    /// <summary>Messaggi dello stream EVENTS per un subject esatto (consumatore effimero, senza ack).</summary>
    private static async Task<IReadOnlyList<Stored>> StreamMessagesAsync(INatsJSContext js, string subject)
    {
        var consumer = await js.CreateOrUpdateConsumerAsync(
            EventNames.EventsStream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = subject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.None,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        var pending = (int)consumer.Info.NumPending;
        var messages = new List<Stored>();
        if (pending == 0)
        {
            return messages;
        }

        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = pending, Expires = TimeSpan.FromSeconds(10) }))
        {
            string? msgId = null;
            if (msg.Headers is { } headers && headers.TryGetValue("Nats-Msg-Id", out var values))
            {
                msgId = values.ToString();
            }

            messages.Add(new Stored(msgId, msg.Data!));
            if (messages.Count >= pending)
            {
                break;
            }
        }

        return messages;
    }
}
