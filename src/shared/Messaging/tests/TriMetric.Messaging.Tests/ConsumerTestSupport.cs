using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Messaging.Tests;

/// <summary>Una chiamata del gestore di prova: busta, tentativo (per quella busta) e nome del payload.</summary>
internal sealed record HandlerCall(Guid Id, string Name, int Attempt, DateTime At);

/// <summary>Contesto passato al gancio del gestore di prova.</summary>
internal sealed record HandlerContext(EventEnvelope<TestPayload> Envelope, int Attempt, TestDbContext Db, CancellationToken Ct);

/// <summary>
/// Comportamento configurabile del gestore di prova (singleton dell'host): registra le chiamate e delega a un gancio
/// che puo' fallire, lanciare o attendere.
/// </summary>
internal sealed class HandlerBehavior
{
    private readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private int _running;
    private int _maxRunning;

    public ConcurrentQueue<HandlerCall> Calls { get; } = new();

    /// <summary>Id delle buste per le quali il gestore e' tornato senza eccezioni (una voce per esecuzione riuscita).</summary>
    public ConcurrentQueue<Guid> Completed { get; } = new();

    /// <summary>Se <c>true</c> (predefinito) il gestore chiama <c>SaveChanges</c>; altrimenti lascia le modifiche pendenti.</summary>
    public bool SaveInHandler { get; set; } = true;

    public Func<HandlerContext, Task>? Hook { get; set; }

    /// <summary>Massimo numero di esecuzioni del gestore osservate in parallelo.</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public int CallCount(Guid id) => Calls.Count(c => c.Id == id);

    public int CompletedCount(Guid id) => Completed.Count(c => c == id);

    internal int NextAttempt(Guid id) => _attempts.AddOrUpdate(id, 1, (_, v) => v + 1);

    internal void Enter()
    {
        var now = Interlocked.Increment(ref _running);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxRunning)))
        {
            Interlocked.CompareExchange(ref _maxRunning, now, seen);
        }
    }

    internal void Exit() => Interlocked.Decrement(ref _running);
}

/// <summary>
/// Gestore di prova: inserisce una riga di dominio (<c>Code</c> = id della busta, univoco) nello STESSO
/// <see cref="TestDbContext"/> dell'inbox, poi esegue il gancio.
/// </summary>
internal sealed class TestHandler(TestDbContext db, HandlerBehavior behavior) : IEventHandler<TestPayload>
{
    public async Task HandleAsync(EventEnvelope<TestPayload> envelope, CancellationToken ct)
    {
        var attempt = behavior.NextAttempt(envelope.Id);
        behavior.Calls.Enqueue(new HandlerCall(envelope.Id, envelope.Data.Name, attempt, DateTime.UtcNow));
        behavior.Enter();
        try
        {
            db.Items.Add(new TestItem { Id = Guid.NewGuid(), Code = envelope.Id.ToString() });
            if (behavior.SaveInHandler)
            {
                await db.SaveChangesAsync(ct);
            }

            if (behavior.Hook is { } hook)
            {
                await hook(new HandlerContext(envelope, attempt, db, ct));
            }

            behavior.Completed.Enqueue(envelope.Id);
        }
        finally
        {
            behavior.Exit();
        }
    }
}

/// <summary>Messaggio letto dallo stream DLQ.</summary>
internal sealed record DlqMessage(ulong Sequence, IReadOnlyDictionary<string, string> Headers, byte[] Data);

/// <summary>Host di prova con <c>AddNatsMessaging</c> e <c>AddEventConsumer</c> sul <see cref="TestDbContext"/>.</summary>
internal sealed class ConsumerHost : IAsyncDisposable
{
    private readonly IHost _host;
    private bool _stopped;

    private ConsumerHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public INatsJSContext JetStream => Services.GetRequiredService<INatsJSContext>();

    public INatsStreamsReadiness Readiness => Services.GetRequiredService<INatsStreamsReadiness>();

    public EventConsumerStats Stats => Services.GetRequiredService<EventConsumerStats>();

    /// <summary><c>StartAsync</c> non deve mai attendere NATS ne' fallire se e' giu'.</summary>
    public static async Task<ConsumerHost> StartAsync(
        string natsUrl,
        string dbConnection,
        ConsumerOptions options,
        HandlerBehavior behavior,
        ILoggerProvider? loggerProvider = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = natsUrl });
        builder.Services.AddDbContext<TestDbContext>(o => o.UseNpgsql(dbConnection).UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton(behavior);
        builder.Services.AddNatsMessaging(builder.Configuration);
        builder.Services.AddEventConsumer<TestDbContext, TestPayload, TestHandler>(options);

        var host = builder.Build();
        await host.StartAsync();
        return new ConsumerHost(host);
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
}

/// <summary>
/// Scenario di un test del consumatore: subject e nome durevole UNICI (i test condividono NATS e il database),
/// tabelle ricreate, pulizia di consumatori e messaggi alla fine.
/// </summary>
internal sealed class ConsumerScenario : IAsyncDisposable
{
    private readonly NatsFixture _nats;
    private readonly TimescaleFixture _db;
    private readonly NatsConnection _connection;
    private readonly List<string> _extraDurables = [];

    private ConsumerScenario(NatsFixture nats, TimescaleFixture db)
    {
        _nats = nats;
        _db = db;
        Durable = $"c{Guid.NewGuid():N}"[..13];

        // Parte casuale diversa da quella del nome durevole: i test di privacy cercano il subject nei log.
        Subject = $"ingestion.test.{Guid.NewGuid():N}"[..27];
        _connection = new NatsConnection(new NatsOpts { Url = nats.Url, MaxReconnectRetry = -1 });
        Js = new NatsJSContext(_connection);
    }

    public string Durable { get; }

    /// <summary>Subject unico: un consumatore con <c>DeliverPolicy.All</c> non vede i messaggi degli altri test.</summary>
    public string Subject { get; }

    public INatsJSContext Js { get; }

    public HandlerBehavior Behavior { get; } = new();

    public string ServiceConnection => _db.ConnectionStringFor("ingestion");

    /// <summary>Opzioni di prova: attese brevi, <c>AckWait</c> ampio (i test che lo vogliono breve lo riducono).</summary>
    public ConsumerOptions Options => new()
    {
        FilterSubject = Subject,
        DurableName = Durable,
        MaxDeliver = 5,
        Backoff = [TimeSpan.FromMilliseconds(100)],
        AckWait = TimeSpan.FromSeconds(10),
        MaxConcurrency = 1,
    };

    public static async Task<ConsumerScenario> CreateAsync(NatsFixture nats, TimescaleFixture db)
    {
        var scenario = new ConsumerScenario(nats, db);
        await TestDbContext.RecreateTablesAsync(scenario.ServiceConnection);
        // Gli stream possono mancare (altri test li cancellano): li crea lo stesso componente usato dal servizio.
        await new NatsStreamsInitializer(scenario.Js, NullLogger<NatsStreamsInitializer>.Instance)
            .EnsureStreamsAsync(CancellationToken.None);
        return scenario;
    }

    public Task<ConsumerHost> StartHostAsync(ConsumerOptions? options = null, ILoggerProvider? logs = null) =>
        ConsumerHost.StartAsync(_nats.Url, ServiceConnection, options ?? Options, Behavior, logs);

    public static EventEnvelope<TestPayload> NewEnvelope(string name = "corsa", int count = 1) => new(
        Id: Guid.NewGuid(),
        Type: EventNames.ActivityImported,
        Version: 1,
        OccurredAt: new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero),
        UserId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        Data: new TestPayload(name, count));

    public static byte[] Serialize(EventEnvelope<TestPayload> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);

    /// <summary>Pubblica la busta; senza <c>Nats-Msg-Id</c> lo stream non deduplica (due messaggi per lo stesso evento).</summary>
    public Task PublishAsync(EventEnvelope<TestPayload> envelope, INatsJSContext? js = null) =>
        PublishRawAsync(Serialize(envelope), js);

    /// <summary>Pubblica il payload grezzo e restituisce la sequenza nello stream EVENTS.</summary>
    public async Task<ulong> PublishRawAsync(byte[] payload, INatsJSContext? js = null)
    {
        var ack = await (js ?? Js).PublishAsync(Subject, payload, NatsRawSerializer<byte[]>.Default);
        ack.EnsureSuccess();
        return ack.Seq;
    }

    /// <summary>Pubblica una voce sulla DLQ di questo consumatore con il <c>Nats-Msg-Id</c> dato (simula una voce gia' presente).</summary>
    public async Task PublishDlqEntryAsync(string msgId, byte[] payload)
    {
        var ack = await Js.PublishAsync(
            $"dlq.{Durable}", payload, NatsRawSerializer<byte[]>.Default, new NatsJSPubOpts { MsgId = msgId });
        ack.EnsureSuccess();
    }

    public async Task<IReadOnlyList<EventEnvelope<TestPayload>>> PublishManyAsync(int count, Func<int, string>? name = null)
    {
        var envelopes = new List<EventEnvelope<TestPayload>>();
        for (var i = 0; i < count; i++)
        {
            var envelope = NewEnvelope(name?.Invoke(i) ?? "corsa", i);
            await PublishAsync(envelope);
            envelopes.Add(envelope);
        }

        return envelopes;
    }

    public async Task<long> InboxCountAsync() =>
        await ScalarAsync("SELECT count(*) FROM ingestion.inbox WHERE consumer = @c");

    public async Task<bool> InboxContainsAsync(Guid id) =>
        await ScalarAsync("SELECT count(*) FROM ingestion.inbox WHERE consumer = @c AND message_id = @m", id) == 1;

    public async Task<long> ItemCountAsync() => await ScalarAsync("SELECT count(*) FROM ingestion.test_items");

    public async Task<long> DeferredCountAsync() => await ScalarAsync("SELECT count(*) FROM ingestion.test_deferred");

    public async Task InsertInboxAsync(Guid id, string? consumer = null)
    {
        await using var connection = new NpgsqlConnection(_db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO ingestion.inbox (consumer, message_id, processed_at) VALUES (@c, @m, now())", connection);
        command.Parameters.AddWithValue("c", consumer ?? Durable);
        command.Parameters.AddWithValue("m", id);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Transazioni aperte e inattive del ruolo del servizio (nessuna transazione appesa dopo l'arresto).</summary>
    public async Task<long> IdleInTransactionCountAsync()
    {
        await using var connection = new NpgsqlConnection(_db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE usename = 'ingestion_svc' AND state LIKE 'idle in transaction%'",
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<ConsumerInfo?> ConsumerInfoAsync(string? durable = null)
    {
        try
        {
            var consumer = await Js.GetConsumerAsync(EventNames.EventsStream, durable ?? Durable);
            return consumer.Info;
        }
        catch (NatsJSApiException)
        {
            return null;
        }
    }

    /// <summary>Consumatore operativo, nessun messaggio in attesa o non confermato.</summary>
    public async Task<bool> IsDrainedAsync()
    {
        var info = await ConsumerInfoAsync();
        return info is { NumPending: 0, NumAckPending: 0 };
    }

    /// <summary>Crea (e ricorda per la pulizia) un consumatore con configurazione propria sullo stream EVENTS.</summary>
    public async Task<INatsJSConsumer> CreateRawConsumerAsync(ConsumerConfig config)
    {
        _extraDurables.Add(config.Name ?? config.DurableName!);
        return await Js.CreateOrUpdateConsumerAsync(EventNames.EventsStream, config);
    }

    /// <summary>Voci della DLQ per questo consumatore (subject <c>dlq.&lt;durable&gt;</c>).</summary>
    public async Task<IReadOnlyList<DlqMessage>> DlqAsync()
    {
        var dlqSubject = $"dlq.{Durable}";
        var consumer = await Js.CreateOrUpdateConsumerAsync(
            EventNames.DlqStream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = dlqSubject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.None,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        var pending = (int)consumer.Info.NumPending;
        var messages = new List<DlqMessage>();
        if (pending == 0)
        {
            return messages;
        }

        await foreach (var msg in consumer.FetchAsync<byte[]>(
            new NatsJSFetchOpts { MaxMsgs = pending, Expires = TimeSpan.FromSeconds(10) },
            NatsRawSerializer<byte[]>.Default))
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (msg.Headers is { } h)
            {
                foreach (var (key, value) in h)
                {
                    headers[key] = value.ToString();
                }
            }

            messages.Add(new DlqMessage(msg.Metadata!.Value.Sequence.Stream, headers, msg.Data ?? []));
            if (messages.Count >= pending)
            {
                break;
            }
        }

        return messages;
    }

    public async Task<int> DlqCountAsync() => (await DlqAsync()).Count;

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var name in _extraDurables.Append(Durable))
            {
                await DeleteConsumerIfExistsAsync(name);
            }

            await Js.PurgeStreamAsync(EventNames.EventsStream, new StreamPurgeRequest { Filter = Subject });
            await Js.PurgeStreamAsync(EventNames.DlqStream, new StreamPurgeRequest { Filter = $"dlq.{Durable}" });
        }
        catch (Exception)
        {
            // Pulizia best effort: un test che ha fermato NATS non deve mascherare il proprio errore.
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private async Task DeleteConsumerIfExistsAsync(string name)
    {
        try
        {
            await Js.DeleteConsumerAsync(EventNames.EventsStream, name);
        }
        catch (NatsJSApiException)
        {
            // Inesistente.
        }
    }

    private async Task<long> ScalarAsync(string sql, Guid? messageId = null)
    {
        await using var connection = new NpgsqlConnection(_db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@c", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("c", Durable);
        }

        if (messageId is { } id)
        {
            command.Parameters.AddWithValue("m", id);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
