namespace TriMetric.Contracts;

/// <summary>
/// Busta comune di ogni evento. Tutti gli istanti sono UTC.
/// </summary>
/// <typeparam name="T">Payload dell'evento.</typeparam>
/// <param name="Id">Identificatore univoco del messaggio (usato per la deduplicazione).</param>
/// <param name="Type">Nome dell'evento, ad esempio <see cref="EventNames.ActivityImported"/>.</param>
/// <param name="Version">Versione del contratto del payload; un cambio incompatibile la incrementa.</param>
/// <param name="OccurredAt">Istante UTC in cui l'evento e' avvenuto.</param>
/// <param name="UserId">Utente proprietario dei dati.</param>
/// <param name="CorrelationId">Identificatore che lega i log lungo tutta la catena di chiamate.</param>
/// <param name="Data">Payload dell'evento.</param>
public sealed record EventEnvelope<T>(
    Guid Id,
    string Type,
    int Version,
    DateTimeOffset OccurredAt,
    Guid UserId,
    Guid CorrelationId,
    T Data);
