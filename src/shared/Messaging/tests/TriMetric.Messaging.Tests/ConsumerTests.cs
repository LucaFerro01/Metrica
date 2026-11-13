using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>
/// Consumatore idempotente: inbox, retry con backoff, DLQ (REQ-014, REQ-015, REQ-017; AC-006, AC-012; DEC-001, DEC-005).
/// Ogni test usa subject e nome durevole unici e attese con polling e timeout.
/// </summary>
[Collection(NatsCollection.Name)]
[Trait("Category", "Consumer")]
public sealed class ConsumerTests(NatsFixture nats, TimescaleFixture db)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private Task<ConsumerScenario> NewScenarioAsync() => ConsumerScenario.CreateAsync(nats, db);

    [Fact]
    public async Task SameEventDeliveredTwice_RunsHandlerOnce_AndAcksTheDuplicate()
    {
        await using var s = await NewScenarioAsync();
        await using var app = await s.StartHostAsync();
        var envelope = ConsumerScenario.NewEnvelope();

        // Due messaggi distinti nello stream (nessun Nats-Msg-Id) con la stessa busta.
        await s.PublishAsync(envelope);
        await s.PublishAsync(envelope);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.Duplicates >= 1 && counters.Processed >= 1, Timeout, "duplicato riconosciuto");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "entrambi i messaggi confermati");

        Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        Assert.Equal(1, await s.ItemCountAsync());
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(0, counters.Retried);
        Assert.Equal(0, counters.DeadLettered);
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task HandlerAlwaysFailing_GoesToDlqAfterMaxDeliver_WithHeaders_AndLaterMessagesAreProcessed()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => ctx.Envelope.Data.Name == "fail"
            ? throw new InvalidOperationException("testo-segreto-dell-eccezione")
            : Task.CompletedTask;
        await using var app = await s.StartHostAsync(s.Options with { MaxDeliver = 3 });

        var failing = ConsumerScenario.NewEnvelope("fail");
        var failingPayload = ConsumerScenario.Serialize(failing);
        await s.PublishRawAsync(failingPayload);
        var good = await s.PublishManyAsync(3);

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1, Timeout, "messaggio in DLQ");
        await Eventually.AssertAsync(
            async () => (await Task.WhenAll(good.Select(g => s.InboxContainsAsync(g.Id)))).All(x => x),
            Timeout,
            "messaggi successivi elaborati");

        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal(failingPayload, dlq.Data);
        Assert.Equal("InvalidOperationException", dlq.Headers["Dlq-Reason"]);
        Assert.Equal(s.Subject, dlq.Headers["Dlq-Original-Subject"]);
        Assert.Equal("3", dlq.Headers["Dlq-Deliveries"]);
        Assert.Equal(s.Durable, dlq.Headers["Dlq-Consumer"]);
        Assert.StartsWith($"{s.Durable}:", dlq.Headers["Nats-Msg-Id"]);

        Assert.Equal(3, s.Behavior.CallCount(failing.Id));
        Assert.False(await s.InboxContainsAsync(failing.Id));
        Assert.Equal(3, await s.ItemCountAsync());
        Assert.Equal(2, app.Stats.For(s.Durable).Retried);
        Assert.Equal(1, app.Stats.For(s.Durable).DeadLettered);
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "nessun messaggio resta in attesa (Term)");
    }

    /// <summary>
    /// Con <c>AckWait</c> breve la ripresa entro 20 s vale anche se il <c>Nak</c> all'arresto non servisse (un arresto non pulito
    /// riprende al piu' dopo <c>AckWait</c>); con <c>AckWait</c> di 60 s la ripresa entro 20 s dimostra che il <c>Nak</c> rilascia
    /// subito il messaggio. In entrambi i casi nessun messaggio e' perso e nessun effetto di dominio e' duplicato.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    public async Task RestartInTheMiddle_LosesNothing_AndDoesNotDuplicateDomainEffects(int ackWaitSeconds)
    {
        await using var s = await NewScenarioAsync();
        using var started = new SemaphoreSlim(0);
        var blocked = false;
        s.Behavior.Hook = async ctx =>
        {
            // Il primo messaggio resta in elaborazione finche l'host non si ferma.
            if (!blocked && Interlocked.Exchange(ref blocked, true) == false)
            {
                started.Release();
                await Task.Delay(Timeout, ctx.Ct);
            }
        };
        var options = s.Options with { AckWait = TimeSpan.FromSeconds(ackWaitSeconds) };

        List<EventEnvelope<TestPayload>> envelopes;
        var logs = new CapturingLoggerProvider();
        await using (var first = await s.StartHostAsync(options, logs))
        {
            envelopes = [.. await s.PublishManyAsync(5)];
            Assert.True(await started.WaitAsync(Timeout), "il primo messaggio non e' arrivato al gestore");
            await first.StopAsync();
        }

        Assert.Equal(0, await s.IdleInTransactionCountAsync());

        await using var second = await s.StartHostAsync(options, logs);
        try
        {
            await Eventually.AssertAsync(
                async () => await s.InboxCountAsync() == 5 && await s.IsDrainedAsync(),
                TimeSpan.FromSeconds(20),
                "tutti i messaggi elaborati dopo il riavvio");
        }
        catch (Exception ex)
        {
            var entries = string.Join(" | ", logs.ComponentEntries.Select(e => $"{e.Level}: {e.Message}"));
            throw new InvalidOperationException($"{ex.Message}. {await s.DescribeAsync(second)}. Log: {entries}", ex);
        }

        foreach (var envelope in envelopes)
        {
            Assert.Equal(1, s.Behavior.CompletedCount(envelope.Id));
            Assert.True(await s.InboxContainsAsync(envelope.Id));
        }

        Assert.Equal(5, await s.ItemCountAsync());
        Assert.Equal(0, await s.DlqCountAsync());
    }

    [Fact]
    public async Task MalformedMessages_GoToDlqAtFirstDelivery_WithoutRetry()
    {
        await using var s = await NewScenarioAsync();
        await using var app = await s.StartHostAsync();

        var emptyId = ConsumerScenario.NewEnvelope() with { Id = Guid.Empty };
        var nullData = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"id":"{{Guid.NewGuid()}}","type":"ActivityImported","version":1,"occurredAt":"2026-11-12T10:30:00+00:00","userId":"{{Guid.NewGuid()}}","correlationId":"{{Guid.NewGuid()}}","data":null}""");
        var payloads = new[]
        {
            System.Text.Encoding.UTF8.GetBytes("questo non e' json {"),
            ConsumerScenario.Serialize(emptyId),
            nullData,
            "null"u8.ToArray(),
            [],
        };
        foreach (var payload in payloads)
        {
            await s.PublishRawAsync(payload);
        }

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == payloads.Length, Timeout, "tutti i malformati in DLQ");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "malformati terminati");

        var dlq = await s.DlqAsync();
        Assert.All(dlq, m =>
        {
            Assert.Equal("Malformed", m.Headers["Dlq-Reason"]);
            Assert.Equal("1", m.Headers["Dlq-Deliveries"]);
        });
        Assert.Equal(payloads.Select(Convert.ToHexString).Order(), dlq.Select(m => Convert.ToHexString(m.Data)).Order());
        Assert.Empty(s.Behavior.Calls);
        Assert.Equal(0, await s.InboxCountAsync());
        Assert.Equal(0, app.Stats.For(s.Durable).Redelivered);
        Assert.Equal(0, app.Stats.For(s.Durable).Retried);
    }

    [Fact]
    public async Task EventValidationException_GoesStraightToDlq_WithoutRetry()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = _ => throw new EventValidationException("colonne di lunghezza diversa");
        await using var app = await s.StartHostAsync();
        var envelope = ConsumerScenario.NewEnvelope();
        var payload = ConsumerScenario.Serialize(envelope);

        await s.PublishRawAsync(payload);

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1, Timeout, "messaggio in DLQ");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio terminato");

        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal("Validation", dlq.Headers["Dlq-Reason"]);
        Assert.Equal("1", dlq.Headers["Dlq-Deliveries"]);
        Assert.Equal(payload, dlq.Data);
        Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        Assert.False(await s.InboxContainsAsync(envelope.Id));
        Assert.Equal(0, await s.ItemCountAsync());
        Assert.Equal(0, app.Stats.For(s.Durable).Retried);
        Assert.Equal(0, app.Stats.For(s.Durable).Redelivered);
    }

    [Fact]
    public async Task ProcessingLongerThanAckWait_IsNotRedelivered_ThanksToAckProgress()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => Task.Delay(TimeSpan.FromSeconds(6), ctx.Ct);
        await using var app = await s.StartHostAsync(s.Options with { AckWait = TimeSpan.FromSeconds(2) });
        var envelope = ConsumerScenario.NewEnvelope();

        await s.PublishAsync(envelope);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.Processed >= 1, TimeSpan.FromSeconds(30), "elaborazione conclusa");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio confermato");

        Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        Assert.Equal(0, counters.Redelivered);
        Assert.Equal(0, counters.Duplicates);
        Assert.Equal(1, await s.InboxCountAsync());
    }

    [Fact]
    public async Task Backlog_WithSlowHandlerAndShortAckWait_DoesNotRedeliverWaitingMessages()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => Task.Delay(200, ctx.Ct);
        await using var app = await s.StartHostAsync(s.Options with { AckWait = TimeSpan.FromSeconds(1) });

        // Pubblicati tutti prima che il primo sia finito: 15 x 200 ms = 3 s di lavoro con AckWait di 1 s.
        var envelopes = await s.PublishManyAsync(15);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.Processed >= 15, TimeSpan.FromSeconds(45), "backlog elaborato");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "tutto confermato");

        Assert.Equal(0, counters.Redelivered);
        Assert.Equal(0, counters.Duplicates);
        Assert.All(envelopes, e => Assert.Equal(1, s.Behavior.CallCount(e.Id)));
        Assert.Equal(15, await s.InboxCountAsync());
    }

    [Fact]
    public async Task HandlerWritingThenThrowing_LeavesNeitherDomainRowNorInboxRow_UntilTheRetrySucceeds()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => ctx.Attempt == 1 ? throw new InvalidOperationException() : Task.CompletedTask;
        // Backoff lungo: tra primo e secondo tentativo lo stato del database e' osservabile senza gara.
        await using var app = await s.StartHostAsync(s.Options with { Backoff = [TimeSpan.FromSeconds(3)] });
        var envelope = ConsumerScenario.NewEnvelope();
        var counters = app.Stats.For(s.Durable);

        await s.PublishAsync(envelope);

        await Eventually.AssertAsync(() => counters.Retried >= 1, Timeout, "primo tentativo fallito");
        Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        Assert.Equal(0, await s.ItemCountAsync());
        Assert.Equal(0, await s.InboxCountAsync());
        Assert.Equal(0, await s.IdleInTransactionCountAsync());

        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "secondo tentativo riuscito");
        Assert.Equal(1, await s.ItemCountAsync());
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(2, s.Behavior.CallCount(envelope.Id));
        Assert.Equal(1, s.Behavior.CompletedCount(envelope.Id));
    }

    [Fact]
    public async Task CommitFailure_IsNotAcked_AndTheMessageIsRetried()
    {
        await using var s = await NewScenarioAsync();
        // Il vincolo differito fa fallire il COMMIT (non il SaveChanges): l'ack non puo' precedere il commit.
        s.Behavior.Hook = async ctx =>
        {
            var inserts = ctx.Attempt == 1 ? 2 : 1;
            for (var i = 0; i < inserts; i++)
            {
                await ctx.Db.Database.ExecuteSqlRawAsync("INSERT INTO ingestion.test_deferred (code) VALUES ('x')", ctx.Ct);
            }
        };
        await using var app = await s.StartHostAsync(s.Options with { Backoff = [TimeSpan.FromSeconds(2)] });
        var envelope = ConsumerScenario.NewEnvelope();
        var counters = app.Stats.For(s.Durable);

        await s.PublishAsync(envelope);

        await Eventually.AssertAsync(() => counters.Retried >= 1, Timeout, "commit fallito e ritentato");
        Assert.Equal(0, await s.InboxCountAsync());
        Assert.Equal(0, await s.DeferredCountAsync());
        Assert.Equal(0, counters.Processed);

        await Eventually.AssertAsync(() => counters.Processed >= 1, Timeout, "secondo tentativo confermato");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio confermato");
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(1, await s.DeferredCountAsync());
        Assert.Equal(2, s.Behavior.CallCount(envelope.Id));
    }

    [Fact]
    public async Task InboxRowAlreadyPresent_HandlerDoesNotRun_AndMessageIsAcked()
    {
        await using var s = await NewScenarioAsync();
        var envelope = ConsumerScenario.NewEnvelope();
        // Equivale a un ack perso dopo il commit: la riga c'e' gia', il messaggio viene riconsegnato.
        await s.InsertInboxAsync(envelope.Id);
        await using var app = await s.StartHostAsync();

        await s.PublishAsync(envelope);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.Duplicates >= 1, Timeout, "duplicato riconosciuto");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "messaggio confermato");

        Assert.Empty(s.Behavior.Calls);
        Assert.Equal(0, await s.ItemCountAsync());
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(0, counters.Redelivered);
        Assert.Equal(0, counters.Retried);
    }

    [Fact]
    public async Task PoisonPill_DeliveredMoreThanMaxDeliver_GoesToDlqWithoutRunningTheHandler()
    {
        await using var s = await NewScenarioAsync();
        var options = s.Options with { MaxDeliver = 2 };
        // Un "processo" che muore a ogni tentativo: il messaggio viene consegnato e mai confermato.
        var raw = await s.CreateRawConsumerAsync(new ConsumerConfig
        {
            Name = s.Durable,
            DurableName = s.Durable,
            FilterSubject = s.Subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            AckWait = options.AckWait,
            MaxAckPending = 1,
            MaxDeliver = -1,
        });
        var envelope = ConsumerScenario.NewEnvelope();
        var payload = ConsumerScenario.Serialize(envelope);
        await s.PublishRawAsync(payload);

        for (var delivery = 1; delivery <= 3; delivery++)
        {
            await foreach (var msg in raw.FetchAsync<byte[]>(
                new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(10) },
                NATS.Client.Core.NatsRawSerializer<byte[]>.Default))
            {
                Assert.Equal((ulong)delivery, msg.Metadata!.Value.NumDelivered);
                await msg.NakAsync();
            }
        }

        await using var app = await s.StartHostAsync(options);

        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1, Timeout, "poison pill in DLQ");
        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal("MaxDeliverExceeded", dlq.Headers["Dlq-Reason"]);
        Assert.Equal("4", dlq.Headers["Dlq-Deliveries"]);
        Assert.Equal(payload, dlq.Data);
        Assert.Empty(s.Behavior.Calls);
        Assert.False(await s.InboxContainsAsync(envelope.Id));
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "poison pill terminato");
    }

    [Fact]
    public async Task Concurrency_ProcessesEachMessageOnce_UpToMaxConcurrency()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => Task.Delay(150, ctx.Ct);
        await using var app = await s.StartHostAsync(s.Options with { MaxConcurrency = 3 });

        var envelopes = await s.PublishManyAsync(30);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.Processed >= 30, TimeSpan.FromSeconds(45), "30 messaggi elaborati");
        await Eventually.AssertAsync(s.IsDrainedAsync, Timeout, "tutto confermato");

        Assert.All(envelopes, e =>
        {
            Assert.Equal(1, s.Behavior.CallCount(e.Id));
            Assert.Equal(1, s.Behavior.CompletedCount(e.Id));
        });
        Assert.Equal(30, await s.InboxCountAsync());
        Assert.Equal(30, await s.ItemCountAsync());
        Assert.InRange(s.Behavior.MaxConcurrent, 2, 3);
        Assert.Equal(0, counters.Duplicates);
        Assert.Equal(0, counters.Redelivered);
    }

    [Fact]
    public async Task HandlerLeavingPendingChanges_HasThemSavedByTheConsumerInTheSameTransaction()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.SaveInHandler = false;
        await using var app = await s.StartHostAsync();
        var envelope = ConsumerScenario.NewEnvelope();

        await s.PublishAsync(envelope);

        await Eventually.AssertAsync(() => app.Stats.For(s.Durable).Processed >= 1, Timeout, "elaborato");
        Assert.Equal(1, await s.ItemCountAsync());
        Assert.True(await s.InboxContainsAsync(envelope.Id));
    }

    [Fact]
    public async Task SameEventIdOnDifferentConsumers_IsProcessedByEach()
    {
        await using var s = await NewScenarioAsync();
        await using var app = await s.StartHostAsync();
        var envelope = ConsumerScenario.NewEnvelope();
        // La PK e' (consumer, message_id): la stessa busta registrata da un altro consumatore non e' un duplicato.
        await s.InsertInboxAsync(envelope.Id, consumer: "altro-consumatore");

        await s.PublishAsync(envelope);

        await Eventually.AssertAsync(() => app.Stats.For(s.Durable).Processed >= 1, Timeout, "elaborato");
        Assert.Equal(1, s.Behavior.CallCount(envelope.Id));
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(0, app.Stats.For(s.Durable).Duplicates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DlqEntryAlreadyPresent_IsTreatedAsSuccess_MessageIsTerminated_AndQueueKeepsFlowing(bool failingHandler)
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => ctx.Envelope.Data.Name == "fail" ? throw new InvalidOperationException() : Task.CompletedTask;
        var payload = failingHandler
            ? ConsumerScenario.Serialize(ConsumerScenario.NewEnvelope("fail"))
            : System.Text.Encoding.UTF8.GetBytes("non json");

        // Il messaggio e' nello stream prima dell'avvio del consumatore; la sua voce DLQ (stesso Nats-Msg-Id che usera' il
        // consumatore) esiste gia': crash tra DLQ e Term, oppure ack della DLQ perso.
        var sequence = await s.PublishRawAsync(payload);
        await s.PublishDlqEntryAsync($"{s.Durable}:{sequence}", [1, 2, 3]);
        await using var app = await s.StartHostAsync(s.Options with { MaxDeliver = 2 });
        var good = await s.PublishManyAsync(2);

        var counters = app.Stats.For(s.Durable);
        await Eventually.AssertAsync(() => counters.DeadLettered >= 1, TimeSpan.FromSeconds(20), "messaggio mandato in DLQ (duplicato = successo)");
        await Eventually.AssertAsync(
            async () => (await Task.WhenAll(good.Select(g => s.InboxContainsAsync(g.Id)))).All(x => x),
            TimeSpan.FromSeconds(20),
            "messaggi successivi elaborati");
        await Eventually.AssertAsync(s.IsDrainedAsync, TimeSpan.FromSeconds(20), "messaggio terminato (Term)");

        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal([1, 2, 3], dlq.Data);
        Assert.Equal(1, counters.DeadLettered);
    }

    [Fact]
    public async Task Backoff_SpacesTheRetriesAtLeastAsConfigured_AndDomainEffectIsWrittenOnce()
    {
        await using var s = await NewScenarioAsync();
        s.Behavior.Hook = ctx => ctx.Attempt <= 3 ? throw new InvalidOperationException() : Task.CompletedTask;
        TimeSpan[] backoff = [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)];
        await using var app = await s.StartHostAsync(s.Options with { Backoff = backoff, MaxDeliver = 5 });
        var envelope = ConsumerScenario.NewEnvelope();

        await s.PublishAsync(envelope);

        await Eventually.AssertAsync(() => app.Stats.For(s.Durable).Processed >= 1, Timeout, "quarto tentativo riuscito");
        var calls = s.Behavior.Calls.Where(c => c.Id == envelope.Id).OrderBy(c => c.Attempt).ToList();
        Assert.Equal([1, 2, 3, 4], calls.Select(c => c.Attempt));
        for (var i = 1; i < calls.Count; i++)
        {
            // Limite inferiore con tolleranza per la risoluzione degli orologi: mai piu' corto del backoff configurato.
            var interval = calls[i].At - calls[i - 1].At;
            Assert.True(
                interval >= backoff[i - 1] - TimeSpan.FromMilliseconds(50),
                $"intervallo {i} troppo corto: {interval.TotalMilliseconds:0} ms < {backoff[i - 1].TotalMilliseconds:0} ms");
        }

        Assert.Equal(1, await s.ItemCountAsync());
        Assert.Equal(1, await s.InboxCountAsync());
        Assert.Equal(1, s.Behavior.CompletedCount(envelope.Id));
        Assert.Equal(3, app.Stats.For(s.Durable).Retried);
        Assert.Equal(0, await s.DlqCountAsync());
    }
}
