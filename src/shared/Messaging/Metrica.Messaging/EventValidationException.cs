namespace Metrica.Messaging;

/// <summary>
/// Lanciata da un <see cref="IEventHandler{T}"/> per un evento non valido: il consumatore annulla la transazione, manda il
/// messaggio direttamente in DLQ (motivo <c>Validation</c>) e non lo ritenta.
/// </summary>
/// <remarks>
/// Il messaggio dell'eccezione non e' mai scritto nei log ne' nella DLQ (potrebbe riportare dati dell'evento): resta per il
/// debugger e per i test del gestore. Il motivo registrato e' solo l'etichetta <c>Validation</c>.
/// </remarks>
public sealed class EventValidationException : Exception
{
    /// <summary>Crea l'eccezione con un messaggio sicuro (senza dati personali).</summary>
    public EventValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Crea l'eccezione con un messaggio sicuro e la causa.</summary>
    public EventValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
