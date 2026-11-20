namespace Metrica.Messaging;

/// <summary>
/// Stato della creazione degli stream JetStream <c>EVENTS</c> e <c>DLQ</c>, che avviene in background:
/// il servizio parte anche con NATS irraggiungibile (REQ-013).
/// </summary>
public interface INatsStreamsReadiness
{
    /// <summary><c>true</c> quando gli stream esistono.</summary>
    bool IsReady { get; }

    /// <summary>Completa quando gli stream esistono; lancia <see cref="OperationCanceledException"/> se annullata prima.</summary>
    Task WhenReadyAsync(CancellationToken cancellationToken);
}
