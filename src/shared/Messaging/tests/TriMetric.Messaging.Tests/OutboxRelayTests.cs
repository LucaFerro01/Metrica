using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using Npgsql;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>Relay dell'outbox verso JetStream: nessuna perdita, nessun doppione, anche con piu' istanze e NATS fermo (REQ-012, REQ-013, AC-005).</summary>
[Collection(NatsCollection.Name)]
[Trait("Category", "Relay")]
public sealed class OutboxRelayTests(NatsFixture nats, TimescaleFixture timescale) : IAsyncLifetime
{
    private const int SixMegabytes = 6 * 1024 * 1024;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly RelayProbe _probe = new(timescale);

    public Task InitializeAsync() => TestDbContext.RecreateTablesAsync(timescale.ConnectionStringFor("ingestion"));

    // Un test che ferma NATS lo lascia acceso per i successivi; un errore di riavvio e' riportato, non ingoiato.
    public Task DisposeAsync() => nats.EnsureRunningAsync();

    private static string NewSubject() => $"ingestion.relay.{Guid.NewGuid():N}";

    private Task<RelayTestHost> StartHostAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        ILoggerProvider? logs = null,
        string? dbConnection = null) =>
        RelayTestHost.StartAsync(nats.Url, dbConnection ?? _probe.ServiceConnection, overrides, logs);

    [Fact]
    public async Task SteadyState_PublishesWithinTwoSecondsAndMarksTheRow()
    {
        var subject = NewSubject();
        // Polling predefinito (500 ms): e' quello che vale in produzione.
        await using var relay = await StartHostAsync(new Dictionary<string, string?> { ["Outbox:PollIntervalMs"] = "500" });
        await relay.WaitWarmAsync();

        var watch = Stopwatch.StartNew();
        var envelope = (await relay.AddEventsAsync(subject, 1))[0];
        await Eventually.AssertAsync(
            async () => await RelayProbe.StreamCountAsync(relay.JetStream, subject) == 1, TimeSpan.FromSeconds(2), "evento nello stream");
        var elapsed = watch.Elapsed;

        await Eventually.AssertAsync(async () => (await _probe.RowAsync(envelope.Id)).PublishedAt is not null, Generous, "PublishedAt");
        var row = await _probe.RowAsync(envelope.Id);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Latenza {elapsed.TotalMilliseconds:0} ms");
        Assert.True(DateTimeOffset.UtcNow - row.PublishedAt < TimeSpan.FromMinutes(1));

        var stored = Assert.Single(await RelayProbe.StreamMessagesAsync(relay.JetStream, subject));
        Assert.Equal(envelope.Id.ToString(), stored.MsgId);
        Assert.Equal(row.Payload, stored.Data);
        Assert.Equal(1, relay.Stats.Published);
        Assert.Equal(0, relay.Stats.Duplicates);
        Assert.Equal(0, relay.Stats.Failures);
    }

    [Fact]
    public async Task NatsStopped_SaveChangesSucceeds_AndMessageIsDeliveredExactlyOnceAfterRestart()
    {
        var subject = NewSubject();
        await nats.StopAsync();
        EventEnvelope<TestPayload> envelope;
        await using (var relay = await StartHostAsync())
        {
            // Il servizio e' partito e non ha lanciato; salvare non richiede il relay ne' NATS.
            envelope = (await relay.AddEventsAsync(subject, 1))[0];
            Assert.Null((await _probe.RowAsync(envelope.Id)).PublishedAt);
            Assert.Equal(0, relay.Stats.Published);

            await nats.StartAsync();

            await Eventually.AssertAsync(async () => (await _probe.RowAsync(envelope.Id)).PublishedAt is not null, Generous, "PublishedAt dopo il riavvio di NATS");
            await relay.WaitCyclesAsync(3, Generous);

            var stored = Assert.Single(await RelayProbe.StreamMessagesAsync(relay.JetStream, subject));
            Assert.Equal(envelope.Id.ToString(), stored.MsgId);
            Assert.Equal(1, relay.Stats.Published);
            Assert.Equal(0, relay.Stats.Duplicates);
            Assert.Equal(0, await _probe.UnpublishedCountAsync());
        }
    }

    [Fact]
    public async Task Startup_WithNatsStopped_ReturnsQuicklyAndWithoutExceptions()
    {
        await nats.StopAsync();
        var watch = Stopwatch.StartNew();

        await using var relay = await StartHostAsync();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Avvio in {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.False(relay.Readiness.IsReady);
        // Il relay aspetta gli stream in background: nessun ciclo ne' tentativo di pubblicazione.
        await Task.Delay(300);
        Assert.Equal(0, relay.Stats.Cycles);
        Assert.Equal(0, relay.Stats.Failures);
    }

    [Fact]
    public async Task TwoConcurrentRelays_PublishEveryMessageExactlyOnce()
    {
        const int Total = 400;
        var subject = NewSubject();
        var small = new Dictionary<string, string?> { ["Outbox:BatchSize"] = "5" };
        await using var first = await StartHostAsync(small);
        await using var second = await StartHostAsync(small);
        await first.WaitWarmAsync();
        await second.WaitWarmAsync();

        // Con batch da 5 e 400 messaggi un relay da solo impiega oltre un secondo, mentre l'altro si sveglia entro
        // 50 ms: entrambi partecipano senza dipendere da una temporizzazione precisa.
        var envelopes = await first.AddEventsAsync(subject, Total);

        await Eventually.AssertAsync(async () => await _probe.UnpublishedCountAsync() == 0, Generous, "tutti marcati");
        await first.WaitCyclesAsync(2, Generous);
        await second.WaitCyclesAsync(2, Generous);

        Assert.True(first.Stats.Published > 0, "il primo relay non ha pubblicato nulla");
        Assert.True(second.Stats.Published > 0, "il secondo relay non ha pubblicato nulla");
        Assert.Equal(Total, first.Stats.Published + second.Stats.Published);
        Assert.Equal(0, first.Stats.Duplicates + second.Stats.Duplicates);
        Assert.Equal(0, first.Stats.Failures + second.Stats.Failures);
        var stored = await RelayProbe.StreamMessagesAsync(first.JetStream, subject);
        Assert.Equal(Total, stored.Count);
        Assert.Equal(envelopes.Select(e => e.Id.ToString()).Order(), stored.Select(m => m.MsgId!).Order());
        Assert.All(await _probe.RowsAsync(), r => Assert.Equal(0, r.Attempts));
    }

    [Fact]
    public async Task RowsLockedByAnotherTransaction_AreSkippedNotWaitedFor_AndPublishedOnceReleased()
    {
        var subject = NewSubject();

        // Le righe esistono e 4 sono bloccate da un'altra "istanza" (transazione di prova) PRIMA che il relay parta:
        // nessuna corsa tra il lock del test e la lettura del relay.
        await using (var seed = _probe.NewContext())
        {
            for (var i = 0; i < 10; i++)
            {
                var envelope = RelayTestHost.NewEnvelope(new TestPayload("corsa", i));
                seed.Outbox.Add(new OutboxMessage
                {
                    Id = envelope.Id,
                    Subject = subject,
                    Type = envelope.Type,
                    Payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options),
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-10).AddMilliseconds(i),
                });
            }

            await seed.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(_probe.ServiceConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT id FROM ingestion.outbox ORDER BY created_at LIMIT 4 FOR UPDATE", connection, transaction))
        {
            await using var reader = await lockCommand.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
            }
        }

        await using var relay = await StartHostAsync();

        await Eventually.AssertAsync(() => relay.Stats.Published >= 6, Generous, "i 6 messaggi non bloccati");
        await relay.WaitCyclesAsync(3, Generous);
        Assert.Equal(6, relay.Stats.Published);
        Assert.Equal(6, await RelayProbe.StreamCountAsync(relay.JetStream, subject));

        await transaction.RollbackAsync();

        await Eventually.AssertAsync(async () => await _probe.UnpublishedCountAsync() == 0, Generous, "i 4 rilasciati");
        Assert.Equal(10, relay.Stats.Published);
        Assert.Equal(0, relay.Stats.Duplicates);
        Assert.Equal(10, await RelayProbe.StreamCountAsync(relay.JetStream, subject));
    }

    [Fact]
    public async Task CrashBetweenPublishAndMark_IsCountedAsDuplicateAndLeavesASingleCopy()
    {
        var subject = NewSubject();
        await using var relay = await StartHostAsync();
        await relay.WaitWarmAsync();

        // Il processo e' morto dopo la pubblicazione e prima della marcatura: il messaggio e' gia' nello stream.
        var id = Guid.NewGuid();
        var preExisting = new byte[] { 1, 2, 3 };
        (await relay.JetStream.PublishAsync(subject, preExisting, opts: new NatsJSPubOpts { MsgId = id.ToString() })).EnsureSuccess();
        await using (var scope = relay.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IOutbox>().Add(subject, RelayTestHost.NewEnvelope(new TestPayload("corsa", 1)) with { Id = id });
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync();
        }

        await Eventually.AssertAsync(async () => (await _probe.RowAsync(id)).PublishedAt is not null, Generous, "PublishedAt");

        Assert.Equal(1, relay.Stats.Duplicates);
        Assert.Equal(1, relay.Stats.Published);
        Assert.Equal(0, relay.Stats.Failures);
        var stored = Assert.Single(await RelayProbe.StreamMessagesAsync(relay.JetStream, subject));
        Assert.Equal(preExisting, stored.Data);
        Assert.Equal(0, (await _probe.RowAsync(id)).Attempts);
    }

    [Fact]
    public async Task SixMegabyteMessage_IsPublished()
    {
        var subject = NewSubject();
        await using var relay = await StartHostAsync(new Dictionary<string, string?> { ["Outbox:PublishTimeoutSeconds"] = "20" });
        await relay.WaitWarmAsync();

        var envelope = (await relay.AddEventsAsync(subject, 1, _ => new TestPayload(new string('x', SixMegabytes), 1)))[0];

        await Eventually.AssertAsync(async () => (await _probe.RowAsync(envelope.Id)).PublishedAt is not null, Generous, "PublishedAt");
        var row = await _probe.RowAsync(envelope.Id);
        Assert.InRange(row.Payload.Length, SixMegabytes, SixMegabytes + 4096);
        var stored = Assert.Single(await RelayProbe.StreamMessagesAsync(relay.JetStream, subject));
        Assert.Equal(row.Payload.Length, stored.Data.Length);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task UnpublishableMessage_RecordsOnlyTheErrorKind_AndDoesNotBlockTheOthersInTheSameBatch()
    {
        var subject = NewSubject();
        var badSubject = $"nostream.{Guid.NewGuid():N}";
        var badId = Guid.NewGuid();
        var goodIds = new List<Guid>();
        await using (var seed = _probe.NewContext())
        {
            // Tre messaggi buoni e uno non pubblicabile (subject fuori da ogni stream) in mezzo, nello stesso batch.
            var created = DateTimeOffset.UtcNow.AddSeconds(-10);
            for (var i = 0; i < 4; i++)
            {
                var envelope = RelayTestHost.NewEnvelope(new TestPayload("corsa", i));
                var bad = i == 1;
                seed.Outbox.Add(new OutboxMessage
                {
                    Id = bad ? badId : envelope.Id,
                    Subject = bad ? badSubject : subject,
                    Type = envelope.Type,
                    Payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options),
                    CreatedAt = created.AddMilliseconds(i),
                });
                if (!bad)
                {
                    goodIds.Add(envelope.Id);
                }
            }

            await seed.SaveChangesAsync();
        }

        await using var relay = await StartHostAsync();

        await Eventually.AssertAsync(() => relay.Stats.Published >= 3, Generous, "i tre messaggi buoni");
        await Eventually.AssertAsync(async () => (await _probe.RowAsync(badId)).Attempts >= 2, Generous, "tentativi crescenti");

        var badRow = await _probe.RowAsync(badId);
        Assert.Null(badRow.PublishedAt);
        Assert.NotNull(badRow.LastError);
        // Solo nome del tipo (e codici del server): niente testo libero, subject, payload o URL.
        Assert.Matches(@"^[A-Za-z0-9_.]+( \(\d+/\d+\))?$", badRow.LastError);
        Assert.DoesNotContain("nostream", badRow.LastError);
        foreach (var id in goodIds)
        {
            var row = await _probe.RowAsync(id);
            Assert.NotNull(row.PublishedAt);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.LastError);
        }

        Assert.Equal(3, await RelayProbe.StreamCountAsync(relay.JetStream, subject));
        Assert.True(relay.Stats.Failures >= 2);
    }

    [Fact]
    public async Task OversizedMessage_DoesNotBlockTheQueue_AndRecordsItsErrorKind()
    {
        const int NineMegabytes = 9 * 1024 * 1024; // oltre max_payload (8 MB) del server
        var subject = NewSubject();
        var hugeId = Guid.NewGuid();
        var goodIds = new List<Guid>();
        await using (var seed = _probe.NewContext())
        {
            // Il messaggio enorme e' il piu' vecchio: sta in testa a ogni batch.
            var created = DateTimeOffset.UtcNow.AddSeconds(-10);
            for (var i = 0; i < 4; i++)
            {
                var id = i == 0 ? hugeId : Guid.NewGuid();
                seed.Outbox.Add(new OutboxMessage
                {
                    Id = id,
                    Subject = subject,
                    Type = EventNames.ActivityImported,
                    Payload = i == 0 ? new byte[NineMegabytes] : [1, 2, 3],
                    CreatedAt = created.AddMilliseconds(i),
                });
                if (i > 0)
                {
                    goodIds.Add(id);
                }
            }

            await seed.SaveChangesAsync();
        }

        await using var relay = await StartHostAsync();

        await Eventually.AssertAsync(() => relay.Stats.Published >= 3, Generous, "i tre messaggi buoni");
        await Eventually.AssertAsync(async () => (await _probe.RowAsync(hugeId)).Attempts >= 2, Generous, "tentativi crescenti");

        var huge = await _probe.RowAsync(hugeId);
        Assert.Null(huge.PublishedAt);
        Assert.Equal("NatsPayloadTooLargeException", huge.LastError);
        foreach (var id in goodIds)
        {
            var row = await _probe.RowAsync(id);
            Assert.NotNull(row.PublishedAt);
            Assert.Equal(0, row.Attempts);
        }

        Assert.Equal(3, await RelayProbe.StreamCountAsync(relay.JetStream, subject));
    }

    [Fact]
    public async Task OrderedSequence_FromASingleRelay_AppearsInTheStreamInCreationOrder()
    {
        var subject = NewSubject();
        await using var relay = await StartHostAsync(new Dictionary<string, string?> { ["Outbox:BatchSize"] = "7" });
        await relay.WaitWarmAsync();
        await nats.StopAsync(); // il relay non pubblica finche non c'e' tutta la sequenza: i batch saranno piu' d'uno
        var ids = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            ids.Add((await relay.AddEventsAsync(subject, 1, _ => new TestPayload("corsa", i)))[0].Id);
        }

        await nats.StartAsync();
        await Eventually.AssertAsync(async () => await _probe.UnpublishedCountAsync() == 0, Generous, "tutti marcati");

        var stored = await RelayProbe.StreamMessagesAsync(relay.JetStream, subject);
        Assert.Equal(ids.Select(i => i.ToString()), stored.OrderBy(m => m.Sequence).Select(m => m.MsgId));
        Assert.Equal(30, relay.Stats.Published);
        Assert.Equal(0, relay.Stats.Duplicates);
    }

    [Fact]
    public async Task StoppingTheHost_WhileNatsDoesNotAnswer_LeavesNoLockAndAnotherRelayTakesOver()
    {
        var subject = NewSubject();
        EventEnvelope<TestPayload> envelope;
        var slow = new Dictionary<string, string?> { ["Outbox:PublishTimeoutSeconds"] = "60" };
        await using (var first = await StartHostAsync(slow))
        {
            await first.WaitWarmAsync();
            await nats.StopAsync();
            envelope = (await first.AddEventsAsync(subject, 1))[0];

            // Il relay ha letto la riga (lock) e aspetta l'ack che non arrivera': transazione aperta e inattiva.
            // Una lettura a vuoto del relay apre e chiude una transazione in pochi millisecondi: si attende che la
            // transazione resti aperta e inattiva in due osservazioni distanti (e' il relay in attesa dell'ack).
            await Eventually.AssertAsync(async () =>
            {
                if (await _probe.IdleInTransactionCountAsync() < 1)
                {
                    return false;
                }

                await Task.Delay(250);
                return await _probe.IdleInTransactionCountAsync() >= 1;
            }, Generous, "transazione del relay aperta e in attesa dell'ack");

            var watch = Stopwatch.StartNew();
            await first.StopAsync();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Arresto in {watch.Elapsed.TotalMilliseconds:0} ms");
        }

        await Eventually.AssertAsync(async () => await _probe.IdleInTransactionCountAsync() == 0, TimeSpan.FromSeconds(10), "nessuna transazione appesa");
        Assert.Null((await _probe.RowAsync(envelope.Id)).PublishedAt);

        await nats.StartAsync();
        await using var second = await StartHostAsync();
        await Eventually.AssertAsync(async () => (await _probe.RowAsync(envelope.Id)).PublishedAt is not null, Generous, "ripreso dal secondo relay");

        Assert.Equal(1, second.Stats.Published);
        Assert.Single(await RelayProbe.StreamMessagesAsync(second.JetStream, subject));
        Assert.Equal(0, await _probe.IdleInTransactionCountAsync());
    }

    [Fact]
    public async Task Backoff_WithNatsStopped_DoesNotBusyLoop()
    {
        var subject = NewSubject();
        await using var relay = await StartHostAsync(new Dictionary<string, string?>
        {
            ["Outbox:PublishTimeoutSeconds"] = "1",
            ["Outbox:MaxBackoffSeconds"] = "30",
        });
        await relay.WaitWarmAsync();
        await nats.StopAsync();
        var envelope = (await relay.AddEventsAsync(subject, 1))[0];

        await Eventually.AssertAsync(() => relay.Stats.Failures >= 1, Generous, "primo fallimento");
        var watch = Stopwatch.StartNew();
        var before = relay.Stats.Failures;
        await Task.Delay(TimeSpan.FromSeconds(6));
        var failuresInWindow = relay.Stats.Failures - before;

        // Timeout di 1 s + pausa 100, 200, 400, 800, ... ms: pochi tentativi, non centinaia (50 ms di polling).
        Assert.InRange(failuresInWindow, 1, 6);
        var row = await _probe.RowAsync(envelope.Id);
        Assert.Null(row.PublishedAt);
        Assert.True(row.Attempts >= 1);
        Assert.NotNull(row.LastError);

        // Primo successo: il backoff riparte da zero e il messaggio viene consegnato.
        await nats.StartAsync();
        await Eventually.AssertAsync(async () => (await _probe.RowAsync(envelope.Id)).PublishedAt is not null, Generous, "consegna dopo il ritorno di NATS");
        Assert.Single(await RelayProbe.StreamMessagesAsync(relay.JetStream, subject));
    }

    [Fact]
    public async Task Logs_DuringFailures_NeverContainPayloadSubjectOrRawExceptionText()
    {
        var logs = new CapturingLoggerProvider();
        var okSubject = NewSubject();
        var badSubject = $"nostream.{Guid.NewGuid():N}";
        const string Marker = "PAYLOAD-MARKER-9f3a";
        await using var relay = await StartHostAsync(
            new Dictionary<string, string?> { ["Outbox:PublishTimeoutSeconds"] = "1" }, logs);
        await relay.WaitWarmAsync();

        // Errore del server (nessuno stream) e poi bus fermo (timeout).
        await using (var seed = _probe.NewContext())
        {
            var envelope = RelayTestHost.NewEnvelope(new TestPayload(Marker, 1));
            seed.Outbox.Add(new OutboxMessage
            {
                Id = envelope.Id,
                Subject = badSubject,
                Type = envelope.Type,
                Payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await Eventually.AssertAsync(() => relay.Stats.Failures >= 1, Generous, "errore del server");
        await nats.StopAsync();
        await relay.AddEventsAsync(okSubject, 1, _ => new TestPayload(Marker, 2));
        var failures = relay.Stats.Failures;
        await Eventually.AssertAsync(() => relay.Stats.Failures >= failures + 1, Generous, "timeout a bus fermo");
        Assert.True(await logs.WaitForComponentEntryAsync(e => e.Level == LogLevel.Warning, TimeSpan.FromSeconds(5)));

        var uri = new Uri(nats.Url);
        var entries = logs.ComponentEntries;
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.DoesNotContain(Marker, entry.Message);
            Assert.DoesNotContain(badSubject, entry.Message);
            Assert.DoesNotContain("nostream", entry.Message);
            Assert.DoesNotContain(okSubject, entry.Message);
            Assert.DoesNotContain("ingestion.relay", entry.Message);
            Assert.DoesNotContain("nats://", entry.Message);
            Assert.DoesNotContain(uri.Port.ToString(), entry.Message);
            Assert.DoesNotContain("Exception:", entry.Message); // nessun testo di eccezione grezzo
            Assert.Null(entry.Exception);
        }
    }

    [Fact]
    public async Task DatabaseErrors_DoNotStopTheRelay_AndLogOnlyTheErrorType()
    {
        var logs = new CapturingLoggerProvider();
        var wrongPassword = new NpgsqlConnectionStringBuilder(_probe.ServiceConnection) { Password = "password-sbagliata-1b7c" }.ConnectionString;
        await using var relay = await StartHostAsync(logs: logs, dbConnection: wrongPassword);
        await Eventually.AssertAsync(() => relay.Readiness.IsReady, Generous, "stream pronti");

        Assert.True(await logs.WaitForComponentEntryAsync(
            e => e.Level == LogLevel.Warning && e.Message.Contains("errore del database"), TimeSpan.FromSeconds(15)));
        await Task.Delay(1000);

        // Il servizio non e' terminato e non martella il database (backoff).
        Assert.InRange(logs.ComponentEntries.Count(e => e.Message.Contains("errore del database")), 1, 8);
        foreach (var entry in logs.ComponentEntries)
        {
            Assert.DoesNotContain("password", entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1b7c", entry.Message);
            Assert.DoesNotContain("ingestion_svc", entry.Message);
            Assert.DoesNotContain("28P01", entry.Message);
            Assert.Null(entry.Exception);
        }

        await relay.StopAsync();
    }

    [Fact]
    public async Task Options_AreValidatedAtStartup()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(new Dictionary<string, string?> { ["Outbox:BatchSize"] = "0" }));
        await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(new Dictionary<string, string?> { ["Outbox:PollIntervalMs"] = "0" }));
    }

    [Fact]
    public void Options_HaveTheDocumentedDefaults()
    {
        var options = new OutboxRelayOptions();

        Assert.Equal(500, options.PollIntervalMs);
        Assert.Equal(50, options.BatchSize);
        Assert.Equal(10, options.PublishTimeoutSeconds);
        Assert.Equal(30, options.MaxBackoffSeconds);
        Assert.Equal("Outbox", OutboxRelayOptions.SectionName);
    }
}
