using Microsoft.EntityFrameworkCore;

namespace Metrica.Messaging;

/// <summary>Mappatura EF Core della tabella <c>inbox</c>.</summary>
public static class InboxModelBuilderExtensions
{
    /// <summary>Nome della tabella, nello schema predefinito del <c>DbContext</c> (nessuno schema esplicito).</summary>
    public const string TableName = "inbox";

    /// <summary>
    /// Mappa <see cref="InboxMessage"/> sulla tabella <c>inbox</c> con colonne in snake_case esplicite
    /// (indipendenti dalla convenzione dei nomi del contesto) e chiave primaria composta <c>(consumer, message_id)</c>.
    /// </summary>
    public static ModelBuilder ApplyInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable(TableName);
            entity.HasKey(m => new { m.Consumer, m.MessageId });

            entity.Property(m => m.MessageId).HasColumnName("message_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
            entity.Property(m => m.Consumer).HasColumnName("consumer").HasColumnType("text").IsRequired();
            entity.Property(m => m.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamp with time zone").IsRequired();
        });

        return modelBuilder;
    }
}
