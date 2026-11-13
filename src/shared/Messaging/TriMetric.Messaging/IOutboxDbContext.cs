using Microsoft.EntityFrameworkCore;

namespace TriMetric.Messaging;

/// <summary>Implementato dal <c>DbContext</c> di un servizio che usa l'outbox (insieme a <see cref="OutboxModelBuilderExtensions.ApplyOutbox"/>).</summary>
public interface IOutboxDbContext
{
    /// <summary>Tabella <c>outbox</c> del servizio.</summary>
    DbSet<OutboxMessage> Outbox { get; }
}
