using Microsoft.EntityFrameworkCore;

namespace TriMetric.Messaging;

/// <summary>Implementato dal <c>DbContext</c> di un servizio che consuma eventi (insieme a <see cref="InboxModelBuilderExtensions.ApplyInbox"/>).</summary>
public interface IInboxDbContext
{
    /// <summary>Tabella <c>inbox</c> del servizio.</summary>
    DbSet<InboxMessage> Inbox { get; }
}
