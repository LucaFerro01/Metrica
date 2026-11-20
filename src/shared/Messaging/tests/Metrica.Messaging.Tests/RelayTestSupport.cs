using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Metrica.Contracts;
using Metrica.TestInfrastructure;
using Xunit;

namespace Metrica.Messaging.Tests;

/// <summary>Messaggio letto dallo stream EVENTS.</summary>
internal sealed record StoredMessage(ulong Sequence, string? MsgId, byte[] Data);

/// <summary>Attese con polling e timeout (mai attese fisse lunghe).</summary>
internal static class Eventually
{
    public static async Task AssertAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(await condition(), $"Condizione non raggiunta entro {timeout.TotalSeconds:0.#} s: {description}");
    }

    public static Task AssertAsync(Func<bool> condition, TimeSpan timeout, string description) =>
        AssertAsync(() => Task.FromResult(condition()), timeout, description);
}

/// <summary>Host di prova con <c>AddNatsMessaging</c>, <c>AddOutbox</c> e <c>AddOutboxRelay</c> sul <see cref="TestDbContext"/>.</summary>
internal sealed class RelayTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private bool _stopped;

    private RelayTestHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public OutboxRelayStats Stats => Services.GetRequiredService<OutboxRelayStats>();

    public INatsJSContext JetStream => Services.GetRequiredService<INatsJSContext>();

    public INatsStreamsReadiness Readiness => Services.GetRequiredService<INatsStreamsReadiness>();

    /// <summary>
    /// Costruisce e avvia l'host (<c>StartAsync</c> non deve attendere NATS). Le opzioni predefinite di prova
    /// hanno un polling breve; <paramref name="overrides"/> le sostituisce.
    /// </summary>
    public static async Task<RelayTestHost> StartAsync(
        string natsUrl,
        string dbConnection,
        IReadOnlyDictionary<string, string?>? overrides = null,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        var settings = new Dictionary<string, string?>
        {
            ["Nats:Url"] = natsUrl,
            ["Outbox:PollIntervalMs"] = "50",
            ["Outbox:PublishTimeoutSeconds"] = "5",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(dbConnection).UseSnakeCaseNamingConvention());
        builder.Services.AddNatsMessaging(builder.Configuration);
        builder.Services.AddOutbox<TestDbContext>();
        builder.Services.AddOutboxRelay<TestDbContext>();

        var host = builder.Build();
        await host.StartAsync();
        return new RelayTestHost(host);
    }

    /// <summary>Attende gli stream e il primo ciclo del relay (modello EF e connessioni gia' pronti).</summary>
    public async Task WaitWarmAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        await Readiness.WhenReadyAsync(cts.Token);
        await Eventually.AssertAsync(() => Stats.Cycles >= 1, timeout ?? TimeSpan.FromSeconds(30), "primo ciclo del relay");
    }

    /// <summary>Attende che il relay completi almeno <paramref name="cycles"/> ulteriori cicli.</summary>
    public Task WaitCyclesAsync(int cycles, TimeSpan timeout)
    {
        var target = Stats.Cycles + cycles;
        return Eventually.AssertAsync(() => Stats.Cycles >= target, timeout, $"{cycles} cicli del relay");
    }

    /// <summary>Scrive eventi nell'outbox con <see cref="IOutbox"/> e un solo <c>SaveChanges</c>.</summary>
    public async Task<IReadOnlyList<EventEnvelope<TestPayload>>> AddEventsAsync(string subject, int count, Func<int, TestPayload>? payload = null)
    {
        await using var scope = Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        var envelopes = new List<EventEnvelope<TestPayload>>();
        for (var i = 0; i < count; i++)
        {
            var envelope = NewEnvelope(payload?.Invoke(i) ?? new TestPayload("corsa", i));
            outbox.Add(subject, envelope);
            envelopes.Add(envelope);
        }

        await scope.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync();
        return envelopes;
    }

    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        await _host.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _host.Dispose();
    }

    public static EventEnvelope<TestPayload> NewEnvelope(TestPayload data) => new(
        Id: Guid.NewGuid(),
        Type: EventNames.ActivityImported,
        Version: 1,
        OccurredAt: new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero),
        UserId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        Data: data);
}

/// <summary>Letture di verifica del database e dello stream, indipendenti dal relay.</summary>
internal sealed class RelayProbe(TimescaleFixture db)
{
    public string ServiceConnection => db.ConnectionStringFor("ingestion");

    public TestDbContext NewContext() => new(TestDbContext.OptionsFor(ServiceConnection));

    public async Task<List<OutboxMessage>> RowsAsync()
    {
        await using var context = NewContext();
        return await context.Outbox.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
    }

    public async Task<OutboxMessage> RowAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.Outbox.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    public async Task<int> UnpublishedCountAsync()
    {
        await using var context = NewContext();
        return await context.Outbox.CountAsync(m => m.PublishedAt == null);
    }

    /// <summary>Transazioni aperte e inattive del ruolo del servizio (lock appesi).</summary>
    public async Task<long> IdleInTransactionCountAsync()
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ingestion_svc' AND state LIKE 'idle in transaction%'",
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Messaggi dello stream EVENTS che corrispondono al subject (consumatore effimero, nessun ack).</summary>
    public static async Task<IReadOnlyList<StoredMessage>> StreamMessagesAsync(INatsJSContext js, string subject)
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

        var pending = consumer.Info.NumPending;
        var messages = new List<StoredMessage>();
        if (pending == 0)
        {
            return messages;
        }

        var watch = Stopwatch.StartNew();
        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = (int)pending, Expires = TimeSpan.FromSeconds(10) }))
        {
            string? msgId = null;
            if (msg.Headers is { } headers && headers.TryGetValue("Nats-Msg-Id", out var values))
            {
                msgId = values.ToString();
            }

            messages.Add(new StoredMessage(msg.Metadata!.Value.Sequence.Stream, msgId, msg.Data!));
            if (messages.Count >= (int)pending || watch.Elapsed > TimeSpan.FromSeconds(15))
            {
                break;
            }
        }

        return messages;
    }

    public static async Task<int> StreamCountAsync(INatsJSContext js, string subject) =>
        (await StreamMessagesAsync(js, subject)).Count;
}
