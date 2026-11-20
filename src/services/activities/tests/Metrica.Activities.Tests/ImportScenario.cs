using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Metrica.Contracts;
using Metrica.Messaging;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>Riga di <c>activities.activities</c>.</summary>
internal sealed record ActivityRow(
    Guid Id, Guid UserId, string Source, string ExternalId, string Sport, DateTime StartTime, double ElapsedSeconds,
    double MovingSeconds, double DistanceMeters, double? ElevationGainMeters, double? AvgHeartRate, double? MaxHeartRate,
    double? AvgPowerWatts, double? AvgCadence, DateTime CreatedAt);

/// <summary>Riga di <c>activities.activity_laps</c>.</summary>
internal sealed record LapRow(Guid Id, Guid ActivityId, DateTime StartTime, double ElapsedSeconds, double DistanceMeters);

/// <summary>Conteggi globali delle tabelle del servizio (per verificare che un evento non abbia scritto nulla).</summary>
internal sealed record TableCounts(long Activities, long Laps, long Points, long Inbox);

/// <summary>Lettura delle tabelle del servizio come superutente (verifica indipendente da quanto fa il codice sotto test).</summary>
internal static class ActivitiesData
{
    public static async Task<List<ActivityRow>> ActivitiesAsync(TimescaleFixture db, Guid userId)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, user_id, source, external_id, sport, start_time, elapsed_seconds, moving_seconds, distance_meters,
                   elevation_gain_meters, avg_heart_rate, max_heart_rate, avg_power_watts, avg_cadence, created_at
            FROM activities.activities WHERE user_id = @u ORDER BY created_at, id
            """,
            connection);
        command.Parameters.AddWithValue("u", userId);

        var rows = new List<ActivityRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ActivityRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetFieldValue<DateTime>(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12), Nullable(reader, 13),
                reader.GetFieldValue<DateTime>(14)));
        }

        return rows;
    }

    public static async Task<List<LapRow>> LapsAsync(TimescaleFixture db, Guid activityId)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, activity_id, start_time, elapsed_seconds, distance_meters FROM activities.activity_laps WHERE activity_id = @a ORDER BY start_time",
            connection);
        command.Parameters.AddWithValue("a", activityId);

        var rows = new List<LapRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LapRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTime>(2), reader.GetDouble(3), reader.GetDouble(4)));
        }

        return rows;
    }

    public static async Task<TableCounts> CountsAsync(TimescaleFixture db) => new(
        await ScalarAsync(db, "SELECT count(*) FROM activities.activities"),
        await ScalarAsync(db, "SELECT count(*) FROM activities.activity_laps"),
        await ScalarAsync(db, "SELECT count(*) FROM activities.activity_stream"),
        await ScalarAsync(db, $"SELECT count(*) FROM activities.inbox WHERE consumer = '{ActivityImportedConsumer.DurableName}'"));

    public static async Task<bool> InboxContainsAsync(TimescaleFixture db, Guid eventId) =>
        await ScalarAsync(db, $"SELECT count(*) FROM activities.inbox WHERE consumer = '{ActivityImportedConsumer.DurableName}' AND message_id = '{eventId}'") == 1;

    private static async Task<long> ScalarAsync(TimescaleFixture db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static double? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}

/// <summary>Messaggio letto dallo stream DLQ.</summary>
internal sealed record DlqEntry(ulong Sequence, IReadOnlyDictionary<string, string> Headers, byte[] Data);

/// <summary>
/// Sonda sul database dell'host (EF Core interceptor registrati con <c>ConfigureDbContext</c>): registra i testi dei comandi
/// (MAI i parametri) e inietta guasti controllati. I guasti sono transitori (un numero di volte) o permanenti (<see cref="Always"/>).
/// </summary>
internal sealed class DatabaseProbe
{
    public const int Always = int.MaxValue;

    /// <summary>Messaggio dell'eccezione iniettata: nessun dato dell'evento (REQ-034).</summary>
    public const string FaultMessage = "guasto-iniettato-dal-test";

    private int _lapInsertFailures;
    private int _commitFailures;
    private int _activityInsertFailures;

    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Istanti (UTC) degli errori iniettati: i tentativi falliti di un evento, in ordine.</summary>
    public ConcurrentQueue<DateTime> Failures { get; } = new();

    /// <summary>Fa fallire <paramref name="times"/> volte l'<c>INSERT</c> dei giri (l'attivita' e' gia' scritta).</summary>
    public void FailLapInserts(int times) => Volatile.Write(ref _lapInsertFailures, times);

    /// <summary>Fa fallire <paramref name="times"/> volte il commit (attivita', giri, punti e inbox gia' scritti nella transazione).</summary>
    public void FailCommits(int times) => Volatile.Write(ref _commitFailures, times);

    /// <summary>Fa fallire <paramref name="times"/> volte l'<c>INSERT</c> dell'attivita'.</summary>
    public void FailActivityInserts(int times) => Volatile.Write(ref _activityInsertFailures, times);

    public int CountCommands(string fragment) =>
        Commands.Count(c => c.Contains(fragment, StringComparison.Ordinal));

    /// <summary>Esecuzioni dell'<c>INSERT</c> nelle attivita' (ogni evento non duplicato la esegue una volta per tentativo).</summary>
    public int ActivityInsertCount => CountCommands("INSERT INTO activities.activities ");

    internal void OnCommand(string rawCommandText)
    {
        // Il provider Npgsql cita gli identificatori solo se serve: si confronta il testo senza virgolette.
        var commandText = rawCommandText.Replace("\"", string.Empty, StringComparison.Ordinal);
        Commands.Enqueue(commandText);
        if (commandText.StartsWith("INSERT INTO activities.activity_laps ", StringComparison.Ordinal) && Consume(ref _lapInsertFailures))
        {
            Fail();
        }

        if (commandText.StartsWith("INSERT INTO activities.activities ", StringComparison.Ordinal) && Consume(ref _activityInsertFailures))
        {
            Fail();
        }
    }

    internal void OnCommit()
    {
        if (Consume(ref _commitFailures))
        {
            Fail();
        }
    }

    private void Fail()
    {
        Failures.Enqueue(DateTime.UtcNow);
        throw new InvalidOperationException(FaultMessage);
    }

    private static bool Consume(ref int budget)
    {
        while (true)
        {
            var current = Volatile.Read(ref budget);
            if (current <= 0)
            {
                return false;
            }

            var next = current == Always ? Always : current - 1;
            if (Interlocked.CompareExchange(ref budget, next, current) == current)
            {
                return true;
            }
        }
    }
}

internal sealed class ProbeCommandInterceptor(DatabaseProbe probe) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        probe.OnCommand(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        probe.OnCommand(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        probe.OnCommand(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        probe.OnCommand(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        probe.OnCommand(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        probe.OnCommand(command.CommandText);
        return ValueTask.FromResult(result);
    }
}

internal sealed class ProbeTransactionInterceptor(DatabaseProbe probe) : DbTransactionInterceptor
{
    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        probe.OnCommit();
        return result;
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        probe.OnCommit();
        return ValueTask.FromResult(result);
    }
}

/// <summary>Logger in memoria di TUTTE le categorie (anche EF Core e NATS.Net): messaggio formattato ed eccezione completa.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception) + (exception is null ? string.Empty : " || " + exception)));
    }
}

/// <summary>
/// Scenario di un test del consumatore di <c>ActivityImported</c> sul NATS e sul database condivisi della collection.
/// </summary>
/// <remarks>
/// <para><b>Isolamento dei consumatori (rischio: ogni host avviato dai test ha il durevole
/// <c>activities-activity-imported</c> sullo stesso NATS).</b> Soluzione deterministica, senza interruttori di produzione:</para>
/// <list type="bullet">
/// <item>la collection non gira in parallelo e i messaggi su <c>ingestion.activity.imported</c> li pubblica SOLO uno scenario;
/// fuori da un test del consumatore il subject e' quindi sempre VUOTO: gli host degli altri test (Production, database
/// temporanei senza tabelle, avvii e arresti ripetuti) avviano un consumatore che non trova niente da elaborare e non puo'
/// pescare messaggi altrui ne' mandarli in DLQ o scriverli sul database sbagliato;</item>
/// <item>all'inizio (<see cref="CreateAsync"/>) e alla fine (<see cref="DisposeAsync"/>) lo scenario CANCELLA il durevole,
/// svuota (<c>PurgeStream</c> con filtro sul subject) <c>EVENTS</c> e la voce DLQ del consumatore: i messaggi restano per sempre
/// in <c>EVENTS</c> (retention a limiti), quindi senza la purga un durevole ricreato rielaborerebbe gli eventi pesanti di
/// test precedenti (100.000 punti);</item>
/// <item>il test dichiara lo scenario PRIMA degli host (<c>await using</c>): gli host si fermano per primi, quindi alla purga
/// finale nessun consumatore e' vivo; se un test fallisce a meta' la pulizia gira comunque.</item>
/// </list>
/// <para>Un host avviato da un altro test crea il durevole e lo lascia dov'e': il test del consumatore successivo lo cancella
/// all'inizio, quindi riparte sempre da un durevole nuovo (<c>DeliverPolicy.All</c>) su uno stream vuoto.</para>
/// </remarks>
internal sealed class ImportScenario : IAsyncDisposable
{
    /// <summary>Nome del consumatore durevole di <c>activities</c> (anche suffisso del subject DLQ).</summary>
    public const string Durable = ActivityImportedConsumer.DurableName;

    private static readonly string DlqSubject = $"dlq.{Durable}";

    private readonly NatsFixture _nats;
    private readonly TimescaleFixture _db;
    private readonly NatsConnection _connection;

    private ImportScenario(NatsFixture nats, TimescaleFixture db)
    {
        _nats = nats;
        _db = db;
        _connection = new NatsConnection(new NatsOpts { Url = nats.Url, MaxReconnectRetry = -1 });
        Js = new NatsJSContext(_connection);
    }

    public INatsJSContext Js { get; }

    /// <summary>Sonda condivisa dagli host avviati con <see cref="StartHost"/> (un host solo per volta).</summary>
    public DatabaseProbe Probe { get; private set; } = new();

    public static async Task<ImportScenario> CreateAsync(NatsFixture nats, TimescaleFixture db)
    {
        var scenario = new ImportScenario(nats, db);
        try
        {
            // Gli stream possono mancare (altri test fermano e riavviano NATS): li crea lo stesso componente del servizio.
            await new NatsStreamsInitializer(scenario.Js, NullLogger<NatsStreamsInitializer>.Instance)
                .EnsureStreamsAsync(CancellationToken.None);
            await scenario.ResetAsync();
            await MigratedDatabase.EnsureAsync(db);
            return scenario;
        }
        catch
        {
            await scenario.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Avvia l'host reale (Development, database condiviso) con il consumatore. <paramref name="startupSettings"/> passa da
    /// <c>UseSetting</c> (vedi la trappola in <see cref="ActivitiesHost"/>). Con <paramref name="freshProbe"/> la sonda riparte da zero.
    /// </summary>
    public ActivitiesHost StartHost(
        IReadOnlyDictionary<string, string?>? startupSettings = null,
        Action<IServiceCollection>? configureServices = null,
        ILoggerProvider? logs = null,
        bool freshProbe = true)
    {
        if (freshProbe)
        {
            Probe = new DatabaseProbe();
        }

        var probe = Probe;
        var host = ActivitiesHost.Development(
            _db,
            _nats,
            startupSettings: startupSettings,
            configureServices: services =>
            {
                services.ConfigureDbContext<ActivitiesDbContext>(options =>
                    options.AddInterceptors(new ProbeCommandInterceptor(probe), new ProbeTransactionInterceptor(probe)));
                configureServices?.Invoke(services);
            },
            logs: logs);
        _ = host.Services;   // costruisce e avvia l'host (e quindi il consumatore)
        return host;
    }

    public static EventConsumerCounters CountersOf(ActivitiesHost host) =>
        host.Services.GetRequiredService<EventConsumerStats>().For(Durable);

    // ---- NATS ---------------------------------------------------------------------------------------------------------

    /// <summary>Pubblica la busta; senza <paramref name="msgId"/> lo stream non deduplica (due messaggi per lo stesso evento).</summary>
    public async Task<PubAckResponse> PublishAsync(EventEnvelope<ActivityImported> envelope, string? msgId = null) =>
        await PublishRawAsync(ImportedEvents.Serialize(envelope), msgId);

    public async Task<PubAckResponse> PublishRawAsync(byte[] payload, string? msgId = null)
    {
        var ack = await Js.PublishAsync(
            EventNames.ActivityImportedSubject,
            payload,
            NatsRawSerializer<byte[]>.Default,
            msgId is null ? null : new NatsJSPubOpts { MsgId = msgId });
        if (msgId is null)
        {
            ack.EnsureSuccess();
        }
        else if (ack.Error is { } error)
        {
            // Con un Nats-Msg-Id esplicito un ack Duplicate e' il risultato atteso (lo legge il chiamante), non un errore.
            throw new NatsJSApiException(error);
        }

        return ack;
    }

    public async Task<ConsumerInfo?> ConsumerInfoAsync()
    {
        try
        {
            return (await Js.GetConsumerAsync(EventNames.EventsStream, Durable)).Info;
        }
        catch (NatsJSApiException)
        {
            return null;
        }
    }

    /// <summary>Consumatore operativo, nessun messaggio in attesa o non confermato.</summary>
    public async Task<bool> IsDrainedAsync() => await ConsumerInfoAsync() is { NumPending: 0, NumAckPending: 0 };

    public async Task DeleteDurableAsync()
    {
        try
        {
            await Js.DeleteConsumerAsync(EventNames.EventsStream, Durable);
        }
        catch (NatsJSApiException)
        {
            // Inesistente.
        }
    }

    /// <summary>Voci della DLQ di questo consumatore (consumatore effimero di sola lettura sullo stream DLQ).</summary>
    public async Task<IReadOnlyList<DlqEntry>> DlqAsync()
    {
        var consumer = await Js.CreateOrUpdateConsumerAsync(
            EventNames.DlqStream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = DlqSubject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.None,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        var pending = (int)consumer.Info.NumPending;
        var entries = new List<DlqEntry>();
        if (pending == 0)
        {
            return entries;
        }

        await foreach (var message in consumer.FetchAsync<byte[]>(
            new NatsJSFetchOpts { MaxMsgs = pending, Expires = TimeSpan.FromSeconds(10) },
            NatsRawSerializer<byte[]>.Default))
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (message.Headers is { } h)
            {
                foreach (var (key, value) in h)
                {
                    headers[key] = value.ToString();
                }
            }

            entries.Add(new DlqEntry(message.Metadata!.Value.Sequence.Stream, headers, message.Data ?? []));
            if (entries.Count >= pending)
            {
                break;
            }
        }

        return entries;
    }

    /// <summary>Numero di voci DLQ del consumatore, dalle informazioni dello stream (nessun consumatore effimero per ogni sondaggio).</summary>
    public async Task<int> DlqCountAsync()
    {
        var info = await Js.GetStreamAsync(EventNames.DlqStream, new StreamInfoRequest { SubjectsFilter = DlqSubject });
        return info.Info.State.Subjects is { } subjects && subjects.TryGetValue(DlqSubject, out var count) ? (int)count : 0;
    }

    // ---- Database -----------------------------------------------------------------------------------------------------

    public Task<List<ActivityRow>> ActivitiesAsync(Guid userId) => ActivitiesData.ActivitiesAsync(_db, userId);

    public Task<List<LapRow>> LapsAsync(Guid activityId) => ActivitiesData.LapsAsync(_db, activityId);

    public Task<long> PointCountAsync(Guid activityId) => StreamTestData.CountAsync(_db, activityId);

    public Task<TableCounts> CountsAsync() => ActivitiesData.CountsAsync(_db);

    public Task<bool> InboxContainsAsync(Guid eventId) => ActivitiesData.InboxContainsAsync(_db, eventId);

    // ---- Pulizia --------------------------------------------------------------------------------------------------------

    private async Task ResetAsync()
    {
        await DeleteDurableAsync();
        await Js.PurgeStreamAsync(EventNames.EventsStream, new StreamPurgeRequest { Filter = EventNames.ActivityImportedSubject });
        await Js.PurgeStreamAsync(EventNames.DlqStream, new StreamPurgeRequest { Filter = DlqSubject });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ResetAsync();
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
}
