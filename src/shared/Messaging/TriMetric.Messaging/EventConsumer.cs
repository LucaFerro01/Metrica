using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;

namespace TriMetric.Messaging;

/// <summary>
/// Consumatore durevole (pull) idempotente di eventi (REQ-014, REQ-015, REQ-017; DEC-001, DEC-005). Per ogni messaggio: in
/// <b>una sola transazione</b> scrive l'id nell'inbox (<c>INSERT ... ON CONFLICT DO NOTHING</c>, quindi l'idempotenza e' del database)
/// ed esegue il <see cref="IEventHandler{T}"/> con lo stesso <c>DbContext</c>; <c>Ack</c> solo dopo il commit.
/// Un duplicato e' confermato senza eseguire il gestore. Un errore annulla la transazione e riprova con <c>NakWithDelay</c>
/// (backoff); superato <see cref="ConsumerOptions.MaxDeliver"/> o per un evento non valido il messaggio va sulla DLQ
/// (<c>dlq.&lt;DurableName&gt;</c>) ed e' terminato senza bloccare i successivi.
/// </summary>
/// <remarks>
/// <b>Nessun <c>MaxDeliver</c> lato server</b> (<c>-1</c>): il limite lo applica il consumatore, che confronta <c>NumDelivered</c> con
/// <see cref="ConsumerOptions.MaxDeliver"/>. Con il limite del server un processo morto durante l'ultimo tentativo farebbe
/// scartare il messaggio dal server senza DLQ (perdita silenziosa). Parte in background senza bloccare l'avvio dell'host e attende
/// gli stream (<see cref="INatsStreamsReadiness"/>); nei log compaiono solo nome del consumatore, sequenza, conteggi, etichette
/// e tipi di eccezione: mai payload, subject o testi di eccezione.
/// </remarks>
public sealed class EventConsumer<TDbContext, TData, THandler> : BackgroundService
    where TDbContext : DbContext, IInboxDbContext
    where THandler : class, IEventHandler<TData>
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(10);

    /// <summary>Tempo massimo di un ack/nak/term: indipendente dall'arresto, cosi' un <c>Ack</c> dopo il commit non e' perso per uno shutdown.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Tempo massimo del commit: indipendente dall'arresto (un commit annullato a meta' ha esito incerto).</summary>
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PullExpires = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PullHeartbeat = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan DlqPublishTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Motivi DLQ non derivati da un'eccezione.</summary>
    private const string ReasonMalformed = "Malformed";

    private const string ReasonValidation = "Validation";
    private const string ReasonMaxDeliverExceeded = "MaxDeliverExceeded";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly INatsJSContext _jetStream;
    private readonly INatsStreamsReadiness _readiness;
    private readonly ConsumerOptions _options;
    private readonly EventConsumerCounters _counters;
    private readonly ILogger<EventConsumer<TDbContext, TData, THandler>> _logger;
    private string? _insertSql;

    internal EventConsumer(
        IServiceScopeFactory scopeFactory,
        INatsJSContext jetStream,
        INatsStreamsReadiness readiness,
        ConsumerOptions options,
        EventConsumerStats stats,
        ILogger<EventConsumer<TDbContext, TData, THandler>> logger)
    {
        _scopeFactory = scopeFactory;
        _jetStream = jetStream;
        _readiness = readiness;
        _options = options;
        _counters = stats.For(options.DurableName);
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Libera subito il chiamante: l'avvio dell'host non deve attendere la rete (REQ-013).
        await Task.Yield();

        try
        {
            await _readiness.WhenReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            try
            {
                var consumer = await _jetStream.CreateOrUpdateConsumerAsync(_options.Stream, BuildConfig(), stoppingToken);
                _logger.LogInformation(
                    "Consumatore {Consumer} avviato sullo stream {Stream} (MaxDeliver {MaxDeliver}, concorrenza {Concurrency}, AckWait {AckWaitMs} ms).",
                    _options.DurableName,
                    _options.Stream,
                    _options.MaxDeliver,
                    _options.MaxConcurrency,
                    (int)_options.AckWait.TotalMilliseconds);

                await ConsumeAsync(consumer, stoppingToken);
                _logger.LogWarning(
                    "Il consumo del consumatore {Consumer} si e' interrotto senza errori: riavvio.",
                    _options.DurableName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (NatsJSApiException ex)
            {
                // Il server ha risposto e rifiutato la configurazione (ad esempio una politica di partenza diversa da un
                // consumatore esistente): non e' un problema di rete. Il testo dell'errore e' del server e non contiene dati
                // personali. Si ritenta (l'operatore puo' correggere senza riavviare il servizio).
                delay = MaxDelay;
                _logger.LogError(
                    "Configurazione del consumatore JetStream {Consumer} rifiutata dal server (codice {Code}, errCode {ErrCode}): {Description}. Nuovo tentativo tra {DelayMs} ms.",
                    _options.DurableName,
                    ex.Error.Code,
                    ex.Error.ErrCode,
                    ex.Error.Description,
                    (int)delay.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                // Solo il tipo dell'eccezione: nessun URL, messaggio o dato che possa contenere credenziali o dati personali.
                if (DateTime.UtcNow - startedAt > HealthyRunThreshold)
                {
                    delay = InitialDelay;
                }

                _logger.LogWarning(
                    "Consumo non disponibile per il consumatore {Consumer} ({ErrorType}): nuovo tentativo tra {DelayMs} ms.",
                    _options.DurableName,
                    ex.GetType().Name,
                    (int)delay.TotalMilliseconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, MaxDelay.TotalMilliseconds));
        }
    }

    private ConsumerConfig BuildConfig() => new()
    {
        Name = _options.DurableName,
        DurableName = _options.DurableName,
        FilterSubject = _options.FilterSubject,
        AckPolicy = ConsumerConfigAckPolicy.Explicit,
        DeliverPolicy = ConsumerConfigDeliverPolicy.All,
        AckWait = _options.AckWait,

        // I messaggi in attesa non sono consegnati: il loro AckWait non scorre e non c'e' riconsegna a vuoto.
        MaxAckPending = _options.MaxConcurrency,

        // Il limite dei tentativi lo applica il consumatore (vedi le note della classe).
        MaxDeliver = -1,
    };

    /// <summary>Legge i messaggi e li elabora fino a <see cref="ConsumerOptions.MaxConcurrency"/> in parallelo; all'uscita attende quelli in corso.</summary>
    private async Task ConsumeAsync(INatsJSConsumer consumer, CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_options.MaxConcurrency);
        var running = new ConcurrentDictionary<int, Task>();
        var nextId = 0;
        try
        {
            await foreach (var message in consumer.ConsumeAsync<byte[]>(
                NatsRawSerializer<byte[]>.Default,
                new NatsJSConsumeOpts
                {
                    MaxMsgs = _options.MaxConcurrency,

                    // Rilevamento rapido di un server riavviato: senza battiti per 2 x IdleHeartbeat il client rifa' le richieste.
                    Expires = PullExpires,
                    IdleHeartbeat = PullHeartbeat,
                },
                stoppingToken))
            {
                try
                {
                    await slots.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Arresto mentre il messaggio attende uno slot (caso raro): lo si rilascia subito invece di attendere AckWait.
                    await SettleAsync(token => message.NakAsync(cancellationToken: token), "Nak", message.Metadata?.Sequence.Stream ?? 0);
                    throw;
                }

                var id = Interlocked.Increment(ref nextId);
                var captured = message;
                running[id] = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await ProcessGuardedAsync(captured, stoppingToken);
                        }
                        finally
                        {
                            running.TryRemove(id, out _);
                            slots.Release();
                        }
                    },
                    CancellationToken.None);
            }
        }
        finally
        {
            // Arresto o errore del consumo: i messaggi in elaborazione terminano (all'arresto vedono la cancellazione).
            await Task.WhenAll(running.Values);
        }
    }

    /// <summary>Un'eccezione inattesa non deve mai fermare il consumo: si registra il tipo e il messaggio resta riconsegnabile.</summary>
    private async Task ProcessGuardedAsync(INatsJSMsg<byte[]> message, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessAsync(message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Errore inatteso nell'elaborazione di un messaggio del consumatore {Consumer} ({ErrorType}): il messaggio resta riconsegnabile.",
                _options.DurableName,
                ex.GetType().Name);
        }
    }

    private async Task ProcessAsync(INatsJSMsg<byte[]> message, CancellationToken stoppingToken)
    {
        var metadata = message.Metadata;
        var deliveries = metadata?.NumDelivered ?? 1;
        var sequence = metadata?.Sequence.Stream ?? 0;
        var payload = message.Data ?? [];
        _counters.AddReceived(deliveries > 1);

        await using var progress = new AckProgressTicker(
            message,
            ProgressInterval(_options.AckWait),
            stoppingToken,
            ex => _logger.LogWarning(
                "AckProgress non inviato per il messaggio {Sequence} del consumatore {Consumer} ({ErrorType}).",
                sequence,
                _options.DurableName,
                ex.GetType().Name));

        // a) Poison pill: il processo e' morto piu' volte durante l'elaborazione. Non si esegue il gestore.
        if (deliveries > (ulong)_options.MaxDeliver)
        {
            await DeadLetterAsync(message, progress, payload, ReasonMaxDeliverExceeded, deliveries, sequence, stoppingToken);
            return;
        }

        // b) Malformato: nessun retry.
        var envelope = TryDeserialize(payload);
        if (envelope is null)
        {
            await DeadLetterAsync(message, progress, payload, ReasonMalformed, deliveries, sequence, stoppingToken);
            return;
        }

        // c) Una transazione: inbox + gestore + SaveChanges finale + commit. Ack solo dopo il commit.
        bool duplicate;
        try
        {
            duplicate = await ExecuteInTransactionAsync(envelope, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Arresto durante l'elaborazione: transazione annullata (nessuna riga di inbox), il messaggio non e' confermato.
            // Un Nak senza attesa lo rende subito riconsegnabile (altrimenti un riavvio attenderebbe AckWait e,
            // con MaxAckPending pieno, nessun altro messaggio arriverebbe nel frattempo).
            await progress.StopAsync();
            await SettleAsync(token => message.NakAsync(cancellationToken: token), "Nak", sequence);
            return;
        }
        catch (EventValidationException)
        {
            // e) Evento non valido: nessun retry. Il testo dell'eccezione non e' registrato.
            await DeadLetterAsync(message, progress, payload, ReasonValidation, deliveries, sequence, stoppingToken);
            return;
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(message, progress, payload, ex, deliveries, sequence, stoppingToken);
            return;
        }

        await progress.StopAsync();
        // Un Ack perso dopo il commit non e' un problema: la riconsegna e' un duplicato (inbox) e viene solo confermata.
        await SettleAsync(token => message.AckAsync(new AckOpts { DoubleAck = true }, token), "Ack", sequence);

        if (duplicate)
        {
            _counters.AddDuplicate();
        }
        else
        {
            _counters.AddProcessed();
        }
    }

    /// <summary>
    /// f) Altra eccezione (gestore, SaveChanges, database, commit): la transazione e' gia' annullata; <c>Nak</c> con backoff
    /// oppure, all'ultimo tentativo, DLQ con il tipo dell'eccezione come motivo. Un commit con esito incerto (connessione persa)
    /// e' innocuo: la riconsegna trova la riga di inbox e viene confermata come duplicato.
    /// </summary>
    private async Task HandleFailureAsync(
        INatsJSMsg<byte[]> message,
        AckProgressTicker progress,
        byte[] payload,
        Exception exception,
        ulong deliveries,
        ulong sequence,
        CancellationToken stoppingToken)
    {
        var reason = OutboxRelayErrors.Describe(exception);
        if (deliveries >= (ulong)_options.MaxDeliver)
        {
            await DeadLetterAsync(message, progress, payload, reason, deliveries, sequence, stoppingToken);
            return;
        }

        var delay = BackoffFor(deliveries);
        _logger.LogWarning(
            "Elaborazione fallita ({ErrorType}) del messaggio {Sequence} del consumatore {Consumer}: tentativo {Delivery} di {MaxDeliver}, nuovo tentativo tra {DelayMs} ms.",
            reason,
            sequence,
            _options.DurableName,
            deliveries,
            _options.MaxDeliver,
            (int)delay.TotalMilliseconds);

        await progress.StopAsync();
        await SettleAsync(token => message.NakAsync(new AckOpts { NakDelay = delay }, token), "Nak", sequence);
        _counters.AddRetried();
    }

    /// <summary>
    /// g) Pubblica il messaggio originale sulla DLQ (<c>Nats-Msg-Id</c> deterministico: un crash tra DLQ e <c>Term</c> non duplica
    /// la voce), attende l'ack dello stream e solo allora termina il messaggio. Se la DLQ non risponde NON termina: <c>Nak</c>
    /// con attesa e si riprova alla riconsegna.
    /// </summary>
    private async Task DeadLetterAsync(
        INatsJSMsg<byte[]> message,
        AckProgressTicker progress,
        byte[] payload,
        string reason,
        ulong deliveries,
        ulong sequence,
        CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(DlqPublishTimeout);

            var headers = new NatsHeaders
            {
                ["Dlq-Reason"] = reason,
                ["Dlq-Original-Subject"] = message.Subject,
                ["Dlq-Deliveries"] = deliveries.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Dlq-Consumer"] = _options.DurableName,
            };
            var ack = await _jetStream.PublishAsync(
                $"dlq.{_options.DurableName}",
                payload,
                NatsRawSerializer<byte[]>.Default,
                new NatsJSPubOpts { MsgId = $"{_options.DurableName}:{sequence}" },
                headers,
                timeout.Token);

            // Un ack con Duplicate = true (stesso Nats-Msg-Id gia' nella DLQ: crash tra DLQ e Term, o ack della DLQ perso) e'
            // un SUCCESSO: la voce c'e' gia' e il messaggio va terminato. EnsureSuccess lo tratterebbe come errore.
            if (ack.Error is { } error)
            {
                throw new NatsJSApiException(error);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await progress.StopAsync();
            await SettleAsync(token => message.NakAsync(cancellationToken: token), "Nak", sequence);
            return;
        }
        catch (Exception ex)
        {
            var delay = BackoffFor(deliveries);
            _logger.LogError(
                "Pubblicazione sulla DLQ non riuscita per il messaggio {Sequence} del consumatore {Consumer} ({ErrorType}): il messaggio non e' terminato, nuovo tentativo tra {DelayMs} ms.",
                sequence,
                _options.DurableName,
                OutboxRelayErrors.Describe(ex),
                (int)delay.TotalMilliseconds);
            await progress.StopAsync();
            await SettleAsync(token => message.NakAsync(new AckOpts { NakDelay = delay }, token), "Nak", sequence);
            return;
        }

        _counters.AddDeadLettered();
        _logger.LogError(
            "Messaggio {Sequence} del consumatore {Consumer} inviato sulla DLQ dopo {Deliveries} consegne (motivo {Reason}).",
            sequence,
            _options.DurableName,
            deliveries,
            reason);

        await progress.StopAsync();
        await SettleAsync(token => message.AckTerminateAsync(new AckOpts { DoubleAck = true }, token), "Term", sequence);
    }

    /// <summary>
    /// Scope nuovo, una transazione: inbox (<c>ON CONFLICT DO NOTHING</c>), gestore, <c>SaveChanges</c> se restano modifiche, commit.
    /// Restituisce <c>true</c> per un duplicato (gestore non eseguito). Un'eccezione lascia la transazione annullata.
    /// </summary>
    private async Task<bool> ExecuteInTransactionAsync(EventEnvelope<TData> envelope, CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();

        // Con una strategia di retry configurata sul contesto le transazioni manuali richiedono questo involucro;
        // senza strategia (predefinito di Npgsql) e' un semplice passaggio.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            token => TransactionCoreAsync(db, handler, envelope, token),
            stoppingToken);
    }

    private async Task<bool> TransactionCoreAsync(TDbContext db, THandler handler, EventEnvelope<TData> envelope, CancellationToken ct)
    {
        // Un eventuale nuovo tentativo della strategia riparte da uno stato pulito.
        db.ChangeTracker.Clear();

        // La dispose senza commit annulla la transazione: nessuna riga di inbox resta scritta.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var inserted = await db.Database.ExecuteSqlRawAsync(
            InsertSql(db),
            [_options.DurableName, envelope.Id, DateTimeOffset.UtcNow],
            ct);
        if (inserted == 0)
        {
            // Duplicato: gia' elaborato da questo consumatore. Il gestore non gira.
            await transaction.RollbackAsync(CancellationToken.None);
            return true;
        }

        await handler.HandleAsync(envelope, ct);
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }

        // Il commit non e' annullato dall'arresto: una cancellazione a meta' lascerebbe un esito incerto.
        using var commitTimeout = new CancellationTokenSource(CommitTimeout);
        await transaction.CommitAsync(commitTimeout.Token);
        return false;
    }

    private EventEnvelope<TData>? TryDeserialize(byte[] payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope<TData>>(payload, ContractJson.Options);
            return envelope is null || envelope.Id == Guid.Empty || envelope.Data is null ? null : envelope;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // JsonException, NotSupportedException, ...: il testo puo' riportare frammenti del payload e non si registra.
            return null;
        }
    }

    /// <summary>Attesa del nuovo tentativo: <c>Backoff[min(NumDelivered - 1, lunghezza - 1)]</c>.</summary>
    private TimeSpan BackoffFor(ulong deliveries)
    {
        var index = (int)Math.Min(deliveries == 0 ? 0 : deliveries - 1, (ulong)(_options.Backoff.Length - 1));
        return _options.Backoff[index];
    }

    /// <summary>Un ack, nak o term con timeout proprio. Un fallimento si registra (solo il tipo) e non lancia: la riconsegna rimedia.</summary>
    private async Task<bool> SettleAsync(Func<CancellationToken, ValueTask> action, string what, ulong sequence)
    {
        using var timeout = new CancellationTokenSource(SettleTimeout);
        try
        {
            await action(timeout.Token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "{Action} non riuscito per il messaggio {Sequence} del consumatore {Consumer} ({ErrorType}): il messaggio sara' riconsegnato.",
                what,
                sequence,
                _options.DurableName,
                ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Intervallo di <c>AckProgress</c>: circa <c>AckWait / 3</c>.</summary>
    private static TimeSpan ProgressInterval(TimeSpan ackWait) =>
        TimeSpan.FromMilliseconds(Math.Max(ackWait.TotalMilliseconds / 3, 25));

    /// <summary>
    /// SQL di inserimento nell'inbox costruito dai metadati del modello EF (tabella, schema e colonne non sono scritti a mano).
    /// </summary>
    private string InsertSql(TDbContext db)
    {
        if (_insertSql is not null)
        {
            return _insertSql;
        }

        var entity = db.Model.FindEntityType(typeof(InboxMessage))
            ?? throw new InvalidOperationException($"{nameof(InboxMessage)} non e' mappato nel modello di {typeof(TDbContext).Name}: serve ApplyInbox().");
        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException($"{nameof(InboxMessage)} non e' mappato su una tabella.");
        var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();

        string Column(string propertyName)
        {
            IProperty property = entity.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"Proprieta' {propertyName} non mappata.");
            return sql.DelimitIdentifier(
                property.GetColumnName(store) ?? throw new InvalidOperationException($"Proprieta' {propertyName} senza colonna."));
        }

        var table = sql.DelimitIdentifier(tableName, entity.GetSchema());
        _insertSql =
            $"INSERT INTO {table} ({Column(nameof(InboxMessage.Consumer))}, {Column(nameof(InboxMessage.MessageId))}, {Column(nameof(InboxMessage.ProcessedAt))}) "
            + "VALUES ({0}, {1}, {2}) ON CONFLICT DO NOTHING";
        return _insertSql;
    }

    /// <summary>
    /// <c>AckProgress</c> periodico durante l'elaborazione. Va fermato (<see cref="StopAsync"/>) prima di <c>Ack</c>, <c>Nak</c> o
    /// <c>Term</c>: un progress in ritardo azzererebbe l'attesa di un <c>Nak</c> con ritardo.
    /// </summary>
    private sealed class AckProgressTicker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _loop;
        private bool _disposed;

        public AckProgressTicker(INatsJSMsg<byte[]> message, TimeSpan interval, CancellationToken stoppingToken, Action<Exception> onFailure)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _loop = Task.Run(() => RunAsync(message, interval, onFailure, _cts.Token), CancellationToken.None);
        }

        public async ValueTask StopAsync()
        {
            await _cts.CancelAsync();
            await _loop;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopAsync();
            _cts.Dispose();
        }

        private static async Task RunAsync(INatsJSMsg<byte[]> message, TimeSpan interval, Action<Exception> onFailure, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(interval);
            var failureLogged = false;
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try
                    {
                        await message.AckProgressAsync(cancellationToken: ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!failureLogged)
                        {
                            failureLogged = true;
                            onFailure(ex);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Fermato.
            }
        }
    }
}
