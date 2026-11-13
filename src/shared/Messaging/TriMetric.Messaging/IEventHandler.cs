using TriMetric.Contracts;

namespace TriMetric.Messaging;

/// <summary>
/// Gestore di un evento. E' registrato <b>scoped</b> e risolto nello stesso scope (quindi con lo stesso <c>DbContext</c> e nella
/// stessa transazione) in cui il consumatore scrive l'inbox: le modifiche del gestore e la riga di inbox sono confermate o annullate insieme.
/// </summary>
/// <typeparam name="T">Payload della busta.</typeparam>
public interface IEventHandler<T>
{
    /// <summary>
    /// Elabora l'evento. Non deve fare <c>Commit</c> ne' aprire transazioni proprie; puo' chiamare <c>SaveChanges</c> (il consumatore
    /// lo chiama alla fine se restano modifiche pendenti). Un'eccezione annulla la transazione e fa ritentare il messaggio;
    /// <see cref="EventValidationException"/> lo manda direttamente in DLQ.
    /// </summary>
    Task HandleAsync(EventEnvelope<T> envelope, CancellationToken ct);
}
