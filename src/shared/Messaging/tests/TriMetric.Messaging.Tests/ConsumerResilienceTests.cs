using System.Text;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>Avvio con NATS fermo, configurazione rifiutata dal server e privacy di log e DLQ del consumatore.</summary>
[Collection(NatsCollection.Name)]
[Trait("Category", "Consumer")]
public sealed class ConsumerResilienceTests(NatsFixture nats, TimescaleFixture db)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Startup_WithNatsStopped_DoesNotThrow_AndConsumesOnceNatsIsBack()
    {
        await using var s = await ConsumerScenario.CreateAsync(nats, db);
        var logs = new CapturingLoggerProvider();
        await nats.StopAsync();
        try
        {
            await using var app = await s.StartHostAsync(logs: logs);
            Assert.False(app.Readiness.IsReady);

            // Con il bus giu' il consumatore attende in background senza errori e senza ciclo serrato.
            await Task.Delay(500);
            Assert.DoesNotContain(logs.ComponentEntries, e => e.Level >= LogLevel.Error);
            Assert.True(logs.ComponentEntries.Count < 20, "troppe voci di log con NATS fermo: ciclo serrato?");

            await nats.StartAsync();
            var envelope = ConsumerScenario.NewEnvelope();
            await Eventually.AssertAsync(
                async () => await s.ConsumerInfoAsync() is not null,
                Timeout,
                "consumatore durevole creato dopo il ritorno di NATS");

            await s.PublishAsync(envelope, app.JetStream);

            await Eventually.AssertAsync(() => app.Stats.For(s.Durable).Processed >= 1, Timeout, "evento consumato");
            Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        }
        finally
        {
            await nats.EnsureRunningAsync();
        }
    }

    [Fact]
    public async Task NatsRestartedWhileConsuming_ConsumerResumesWithoutRestartingTheHost()
    {
        await using var s = await ConsumerScenario.CreateAsync(nats, db);
        await using var app = await s.StartHostAsync();
        var before = ConsumerScenario.NewEnvelope();
        await s.PublishAsync(before);
        await Eventually.AssertAsync(() => s.Behavior.CompletedCount(before.Id) == 1, Timeout, "primo evento");

        await nats.StopAsync();
        try
        {
            await nats.StartAsync();
        }
        finally
        {
            await nats.EnsureRunningAsync();
        }

        var after = ConsumerScenario.NewEnvelope();
        await s.PublishAsync(after, app.JetStream);

        await Eventually.AssertAsync(
            () => s.Behavior.CompletedCount(after.Id) == 1,
            TimeSpan.FromSeconds(60),
            "evento consumato dopo il riavvio di NATS");
        Assert.Equal(1, s.Behavior.CompletedCount(before.Id));
    }

    [Fact]
    public async Task Startup_WithNatsAlreadyDown_StartAsyncReturnsPromptly()
    {
        await using var s = await ConsumerScenario.CreateAsync(nats, db);
        await nats.StopAsync();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await using var app = await s.StartHostAsync();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"avvio lento: {watch.Elapsed}");
        }
        finally
        {
            await nats.EnsureRunningAsync();
        }
    }

    [Fact]
    public async Task ConsumerConfigurationRejectedByServer_LogsErrorWithCode_AndDoesNotCrash()
    {
        await using var s = await ConsumerScenario.CreateAsync(nats, db);
        var logs = new CapturingLoggerProvider();
        // Stesso nome, ma con una politica di partenza diversa: il server non la fa cambiare su un consumatore esistente.
        await s.CreateRawConsumerAsync(new ConsumerConfig
        {
            Name = s.Durable,
            DurableName = s.Durable,
            FilterSubject = s.Subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New,
        });

        await using var app = await s.StartHostAsync(logs: logs);

        Assert.True(await logs.WaitForComponentEntryAsync(
            e => e.Level == LogLevel.Error && e.Message.Contains("rifiutata dal server"), TimeSpan.FromSeconds(15)));
        var entry = logs.ComponentEntries.First(e => e.Level == LogLevel.Error && e.Message.Contains("rifiutata dal server"));
        Assert.Matches(@"codice \d+, errCode \d+", entry.Message);
        Assert.DoesNotContain(s.Subject, entry.Message);
        Assert.DoesNotContain(logs.ComponentEntries, e => e.Message.Contains("NATS non disponibile"));

        // Nessun crash: l'host e' vivo e StopAsync termina senza eccezioni (fatto dal DisposeAsync).
        Assert.NotNull(app.Services);
    }

    [Fact]
    public async Task LogsAndDlq_NeverContainPayloadSubjectOrRawExceptionText()
    {
        await using var s = await ConsumerScenario.CreateAsync(nats, db);
        const string SecretException = "SEGRETO-ECCEZIONE lat=45.4642 lon=9.1900";
        const string SecretPayload = "SEGRETO-PAYLOAD-coordinate-45.4642";
        s.Behavior.Hook = ctx => ctx.Envelope.Data.Name == "fail"
            ? throw new InvalidOperationException(SecretException)
            : ctx.Envelope.Data.Name == "invalid"
                ? throw new EventValidationException(SecretException)
                : Task.CompletedTask;
        var logs = new CapturingLoggerProvider();
        await using var app = await s.StartHostAsync(s.Options with { MaxDeliver = 2 }, logs);

        await s.PublishAsync(ConsumerScenario.NewEnvelope() with { Data = new TestPayload("fail", 1) });
        await s.PublishAsync(ConsumerScenario.NewEnvelope() with { Data = new TestPayload("invalid", 1) });
        await s.PublishRawAsync(Encoding.UTF8.GetBytes($$"""{"id":"{{Guid.NewGuid()}}","data":"{{SecretPayload}}" """));
        await s.PublishAsync(ConsumerScenario.NewEnvelope(SecretPayload));

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 3, Timeout, "tre messaggi in DLQ");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "tutto terminato");

        var allowedReasons = new[] { "InvalidOperationException", "Validation", "Malformed" };
        foreach (var message in await s.DlqAsync())
        {
            foreach (var (key, value) in message.Headers)
            {
                Assert.DoesNotContain("SEGRETO", value);
                Assert.DoesNotContain("45.4642", value);
                if (key == "Dlq-Reason")
                {
                    Assert.Contains(value, allowedReasons);
                }
            }

            Assert.Equal(
                new[] { "Dlq-Consumer", "Dlq-Deliveries", "Dlq-Original-Subject", "Dlq-Reason", "Nats-Msg-Id" },
                message.Headers.Keys.Order(StringComparer.Ordinal));
        }

        Assert.NotEmpty(logs.ComponentEntries);
        foreach (var entry in logs.ComponentEntries)
        {
            Assert.DoesNotContain("SEGRETO", entry.Message);
            Assert.DoesNotContain("45.4642", entry.Message);
            Assert.DoesNotContain(s.Subject, entry.Message);
            Assert.DoesNotContain(s.Subject.Split('.')[^1], entry.Message);
            Assert.Null(entry.Exception);
        }
    }
}
