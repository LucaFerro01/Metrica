using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;

namespace TriMetric.Messaging;

/// <summary>
/// Crea in modo idempotente (<c>CreateOrUpdate</c>) gli stream <c>EVENTS</c> e <c>DLQ</c> (DEC-005).
/// Gira in background con tentativi a backoff e non lancia mai al boot: se NATS e' irraggiungibile il servizio
/// si avvia comunque (REQ-013) e gli stream vengono creati appena il bus risponde.
/// </summary>
public sealed class NatsStreamsInitializer(INatsJSContext jetStream, ILogger<NatsStreamsInitializer> logger)
    : BackgroundService, INatsStreamsReadiness
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <inheritdoc />
    public Task WhenReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Un tentativo di creazione o aggiornamento di entrambi gli stream; lancia se NATS non risponde.
    /// Ripetibile senza effetti collaterali.
    /// </summary>
    public async Task EnsureStreamsAsync(CancellationToken cancellationToken)
    {
        // I subject di EventNames sono array mutabili: si passa sempre una copia.
        await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(EventNames.EventsStream, [.. EventNames.EventsStreamSubjects])
            {
                Storage = StreamConfigStorage.File,
                DuplicateWindow = TimeSpan.FromHours(1),
                // Nessun MaxMsgSize (-1, predefinito): vale il max_payload del server (REQ-036, DEC-012).
            },
            cancellationToken);

        await jetStream.CreateOrUpdateStreamAsync(
            new StreamConfig(EventNames.DlqStream, [.. EventNames.DlqStreamSubjects])
            {
                Storage = StreamConfigStorage.File,
            },
            cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Libera subito il chiamante: l'avvio dell'host non deve attendere la rete.
        await Task.Yield();

        var delay = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureStreamsAsync(stoppingToken);
                _ready.TrySetResult();
                logger.LogInformation("Stream JetStream {Events} e {Dlq} pronti.", EventNames.EventsStream, EventNames.DlqStream);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (NatsJSApiException ex)
            {
                // Il server ha risposto e rifiutato la configurazione (ad esempio storage diverso da uno stream esistente):
                // non e' un problema di rete. Il testo dell'errore e' del server e non contiene dati personali.
                // Si ritenta comunque (puo' essere corretto dall'operatore senza riavviare il servizio).
                logger.LogError(
                    "Configurazione dello stream JetStream rifiutata dal server (codice {Code}, errCode {ErrCode}): {Description}. Nuovo tentativo tra {DelayMs} ms.",
                    ex.Error.Code,
                    ex.Error.ErrCode,
                    ex.Error.Description,
                    (int)MaxDelay.TotalMilliseconds);
                delay = MaxDelay;
            }
            catch (Exception ex)
            {
                // Solo il tipo dell'eccezione: nessun URL, messaggio o dato che possa contenere credenziali o dati personali.
                logger.LogWarning(
                    "NATS non disponibile ({ErrorType}): creazione degli stream rimandata, nuovo tentativo tra {DelayMs} ms.",
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
}
