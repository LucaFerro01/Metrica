using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace TriMetric.Messaging;

/// <summary>
/// Relay dell'outbox (REQ-012, REQ-013): legge i messaggi non pubblicati, li pubblica su JetStream e li marca.
/// Piu' istanze (processi o host) possono girare sullo stesso database: la lettura usa
/// <c>FOR UPDATE SKIP LOCKED</c> in una transazione che dura quanto il batch, quindi ogni istanza elabora righe diverse.
/// La consegna e' <b>at-least-once</b> con deduplicazione lato server: l'id del messaggio e' il <c>Nats-Msg-Id</c>, quindi un
/// crash tra pubblicazione e marcatura non produce doppioni entro la finestra di deduplicazione dello stream.
/// Parte in background senza bloccare l'avvio dell'host e attende gli stream (<see cref="INatsStreamsReadiness"/>).
/// </summary>
public sealed class OutboxRelay<TDbContext>(
    IServiceScopeFactory scopeFactory,
    INatsJSContext jetStream,
    INatsStreamsReadiness readiness,
    IOptions<OutboxRelayOptions> options,
    OutboxRelayStats stats,
    ILogger<OutboxRelay<TDbContext>> logger) : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    /// <summary>Tempo massimo del salvataggio finale delle marcature, indipendente dall'arresto dell'host.</summary>
    private static readonly TimeSpan FinalSaveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Intervallo minimo tra due log di warning identici (un messaggio problematico non deve inondare i log).</summary>
    private static readonly TimeSpan WarningThrottle = TimeSpan.FromSeconds(10);

    private readonly OutboxRelayOptions _options = options.Value;
    private string? _selectSql;
    private string? _lastWarningKey;
    private DateTime _lastWarningAt;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Libera subito il chiamante: l'avvio dell'host non deve attendere la rete (REQ-013).
        await Task.Yield();

        try
        {
            await readiness.WhenReadyAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("Relay dell'outbox avviato (batch {BatchSize}, polling {PollIntervalMs} ms).", _options.BatchSize, _options.PollIntervalMs);

        var pacer = new RelayPacer(_options.PollIntervalMs, _options.BatchSize, _options.MaxBackoffSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var result = await RelayBatchAsync(stoppingToken);
                stats.AddCycle();
                delay = pacer.AfterBatch(result.Read, result.Published, result.Failed);

                if (result.Failed > 0)
                {
                    Warn(
                        $"publish:{result.ErrorKinds}",
                        "Relay dell'outbox: {Failed} pubblicazioni fallite su {Read} messaggi ({ErrorKinds}); {Published} pubblicati; nuovo tentativo tra {DelayMs} ms.",
                        result.Failed,
                        result.Read,
                        result.ErrorKinds,
                        result.Published,
                        (int)delay.TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database non raggiungibile, connessione persa, ecc.: il servizio non termina. Solo il tipo dell'eccezione.
                delay = pacer.AfterDatabaseError();
                Warn(
                    $"db:{ex.GetType().Name}",
                    "Relay dell'outbox: errore del database ({ErrorType}); nuovo tentativo tra {DelayMs} ms.",
                    ex.GetType().Name,
                    (int)delay.TotalMilliseconds);
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Un batch: lettura con lock, pubblicazione in ordine, marcatura e commit in una sola transazione.</summary>
    private async Task<BatchResult> RelayBatchAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        // Con una strategia di retry configurata sul contesto le transazioni manuali richiedono questo involucro;
        // senza strategia (predefinito di Npgsql) e' un semplice passaggio.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(
            token => RelayBatchCoreAsync(db, token),
            stoppingToken);
    }

    private async Task<BatchResult> RelayBatchCoreAsync(TDbContext db, CancellationToken stoppingToken)
    {
        // Un eventuale nuovo tentativo della strategia riparte da uno stato pulito.
        db.ChangeTracker.Clear();

        await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
        var messages = await db.Outbox
            .FromSqlRaw(SelectSql(db), _options.BatchSize)
            .AsTracking()
            .ToListAsync(stoppingToken);

        int published = 0, duplicates = 0, failed = 0;
        var errorKinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            try
            {
                var ack = await PublishAsync(message, stoppingToken);
                message.PublishedAt = DateTimeOffset.UtcNow;
                published++;
                if (ack.Duplicate)
                {
                    duplicates++;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Arresto durante la pubblicazione: il messaggio in volo resta non pubblicato (e non modificato)
                // e sara' ripreso da un'altra istanza; le marcature gia' confermate si salvano sotto.
                break;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = OutboxRelayErrors.Describe(ex);
                errorKinds.Add(message.LastError);
                failed++;
                stats.AddFailure();

                if (OutboxRelayErrors.IsBusUnavailable(ex))
                {
                    // Il bus non risponde: gli altri messaggi del batch farebbero la stessa fine (e ognuno terrebbe il
                    // lock per un timeout intero). Si ritenta dopo la pausa.
                    break;
                }
            }
        }

        if (published + failed > 0)
        {
            await SaveAndCommitAsync(db, transaction, stoppingToken);
        }

        // Contatori solo a commit riuscito: un salvataggio fallito significa messaggi ripubblicati (duplicati).
        stats.AddPublished(published, duplicates);
        return new BatchResult(messages.Count, published, failed, string.Join(", ", errorKinds));
    }

    /// <summary>
    /// Salva le marcature (anche dei messaggi gia' confermati dal bus) con un token <b>indipendente dall'arresto</b> e un
    /// timeout breve: un arresto durante il salvataggio non deve far perdere un batch gia' pubblicato (verrebbe
    /// ripubblicato e confermato come duplicato). Durante l'arresto un errore e' ininfluente: la transazione viene comunque
    /// chiusa (rollback alla dispose) e nessun lock resta appeso; fuori dall'arresto l'errore si propaga.
    /// </summary>
    private static async Task SaveAndCommitAsync(TDbContext db, IDbContextTransaction transaction, CancellationToken stoppingToken)
    {
        using var flush = new CancellationTokenSource(FinalSaveTimeout);
        try
        {
            await db.SaveChangesAsync(flush.Token);
            await transaction.CommitAsync(flush.Token);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Le marcature non salvate sono ripubblicate dopo l'arresto e confermate come duplicati (Nats-Msg-Id).
            throw new OperationCanceledException(stoppingToken);
        }
    }

    private async Task<PubAckResponse> PublishAsync(OutboxMessage message, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));
        try
        {
            // Byte grezzi, nessuna ri-serializzazione; l'id del messaggio e' il Nats-Msg-Id.
            return await jetStream.PublishAsync(
                message.Subject,
                message.Payload,
                NatsRawSerializer<byte[]>.Default,
                new NatsJSPubOpts { MsgId = message.Id.ToString() },
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    /// <summary>
    /// SQL di lettura costruito dai metadati del modello EF (tabella, schema e colonne non sono scritti a mano).
    /// <c>SKIP LOCKED</c> salta le righe bloccate dalle altre istanze invece di attenderle.
    /// </summary>
    private string SelectSql(TDbContext db)
    {
        if (_selectSql is not null)
        {
            return _selectSql;
        }

        var entity = db.Model.FindEntityType(typeof(OutboxMessage))
            ?? throw new InvalidOperationException($"{typeof(OutboxMessage).Name} non e' mappato nel modello di {typeof(TDbContext).Name}: serve ApplyOutbox().");
        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(OutboxMessage).Name} non e' mappato su una tabella.");
        var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();

        string Column(IProperty property) => sql.DelimitIdentifier(
            property.GetColumnName(store) ?? throw new InvalidOperationException($"Proprieta' {property.Name} senza colonna."));

        var columns = string.Join(", ", entity.GetProperties().Select(Column));
        var published = Column(entity.FindProperty(nameof(OutboxMessage.PublishedAt))!);
        var created = Column(entity.FindProperty(nameof(OutboxMessage.CreatedAt))!);
        var table = sql.DelimitIdentifier(tableName, entity.GetSchema());

        _selectSql = $"SELECT {columns} FROM {table} WHERE {published} IS NULL ORDER BY {created} LIMIT {{0}} FOR UPDATE SKIP LOCKED";
        return _selectSql;
    }

    /// <summary>Warning con limite di frequenza: lo stesso problema si ripete al massimo ogni <see cref="WarningThrottle"/>.</summary>
    private void Warn(string key, string message, params object[] args)
    {
        var now = DateTime.UtcNow;
        if (key == _lastWarningKey && now - _lastWarningAt < WarningThrottle)
        {
            return;
        }

        _lastWarningKey = key;
        _lastWarningAt = now;
        logger.LogWarning(message, args);
    }

    private readonly record struct BatchResult(int Read, int Published, int Failed, string ErrorKinds);
}
