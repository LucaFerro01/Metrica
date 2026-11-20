using Metrica.Contracts;

namespace Metrica.Messaging;

/// <summary>
/// Accoda un evento nell'outbox del <c>DbContext</c> del chiamante. Non chiama <c>SaveChanges</c>:
/// la riga e' scritta dal <c>SaveChanges</c> del chiamante, quindi nella stessa transazione dei dati di dominio.
/// </summary>
public interface IOutbox
{
    /// <summary>
    /// Aggiunge la busta al change tracker; <c>OutboxMessage.Id</c> e' <c>envelope.Id</c>.
    /// </summary>
    /// <remarks>
    /// Una busta con lo stesso <c>Id</c> nello stesso contesto lancia gia' qui <see cref="InvalidOperationException"/>
    /// (change tracker); con contesti diversi il fallimento e' alla <c>SaveChanges</c>, per violazione della chiave
    /// primaria (PostgreSQL 23505): mai una sovrascrittura silenziosa.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <c>Id</c> vuoto, <c>Type</c> vuoto, o <paramref name="subject"/> fuori dai prefissi di <c>EventNames.EventsStreamSubjects</c>
    /// (non sarebbe mai pubblicabile).
    /// </exception>
    void Add<T>(string subject, EventEnvelope<T> envelope);
}
