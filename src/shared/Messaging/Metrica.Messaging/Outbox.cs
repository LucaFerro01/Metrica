using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Metrica.Contracts;

namespace Metrica.Messaging;

/// <summary>Implementazione di <see cref="IOutbox"/> sul <typeparamref name="TDbContext"/> dello scope corrente.</summary>
internal sealed class Outbox<TDbContext>(TDbContext context) : IOutbox
    where TDbContext : DbContext, IOutboxDbContext
{
    public void Add<T>(string subject, EventEnvelope<T> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Id == Guid.Empty)
        {
            throw new ArgumentException("L'id della busta non puo' essere vuoto (e' il Nats-Msg-Id).", nameof(envelope));
        }

        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            throw new ArgumentException("Il tipo della busta non puo' essere vuoto.", nameof(envelope));
        }

        EnsureSubjectBelongsToEventsStream(subject);

        context.Outbox.Add(new OutboxMessage
        {
            Id = envelope.Id,
            Subject = subject,
            Type = envelope.Type,
            Payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options),
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Un subject fuori dai prefissi di <see cref="EventNames.EventsStreamSubjects"/> non sarebbe accettato da alcuno
    /// stream: il relay non riuscirebbe mai a pubblicarlo.
    /// </summary>
    private static void EnsureSubjectBelongsToEventsStream(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        foreach (var pattern in EventNames.EventsStreamSubjects)
        {
            var prefix = pattern.TrimEnd('>'); // "ingestion.>" -> "ingestion."
            if (subject.Length > prefix.Length && subject.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new ArgumentException(
            $"Il subject '{subject}' non appartiene allo stream {EventNames.EventsStream} "
            + $"(prefissi ammessi: {string.Join(", ", EventNames.EventsStreamSubjects)}).",
            nameof(subject));
    }
}
