using Microsoft.EntityFrameworkCore;

namespace Metrica.Messaging;

/// <summary>Mappatura EF Core della tabella <c>outbox</c>.</summary>
public static class OutboxModelBuilderExtensions
{
    /// <summary>Nome della tabella, nello schema predefinito del <c>DbContext</c> (nessuno schema esplicito).</summary>
    public const string TableName = "outbox";

    /// <summary>Nome dell'indice parziale sui messaggi non ancora pubblicati.</summary>
    public const string UnpublishedIndexName = "ix_outbox_unpublished";

    /// <summary>
    /// Mappa <see cref="OutboxMessage"/> sulla tabella <c>outbox</c> con colonne in snake_case esplicite
    /// (indipendenti dalla convenzione dei nomi del contesto) e l'indice parziale <c>ix_outbox_unpublished</c>.
    /// </summary>
    public static ModelBuilder ApplyOutbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable(TableName);
            entity.HasKey(m => m.Id);

            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.Subject).HasColumnName("subject").HasColumnType("text").IsRequired();
            entity.Property(m => m.Type).HasColumnName("type").HasColumnType("text").IsRequired();
            entity.Property(m => m.Payload).HasColumnName("payload").HasColumnType("bytea").IsRequired();
            entity.Property(m => m.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(m => m.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
            entity.Property(m => m.Attempts).HasColumnName("attempts").HasColumnType("integer").IsRequired()
                .HasDefaultValue(0).ValueGeneratedNever();
            entity.Property(m => m.LastError).HasColumnName("last_error").HasColumnType("text");

            // Il relay legge solo i non pubblicati in ordine di creazione: l'indice contiene solo quelli.
            entity.HasIndex(m => m.CreatedAt)
                .HasDatabaseName(UnpublishedIndexName)
                .HasFilter("\"published_at\" IS NULL");
        });

        return modelBuilder;
    }
}
