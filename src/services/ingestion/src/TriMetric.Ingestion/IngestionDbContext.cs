using Microsoft.EntityFrameworkCore;
using TriMetric.Messaging;

namespace TriMetric.Ingestion;

/// <summary>
/// Database del servizio (schema <c>ingestion</c>, ruolo <c>ingestion_svc</c>): registro dei caricamenti e outbox.
/// I nomi di tabelle e colonne sono in snake_case (convenzione impostata alla registrazione del contesto).
/// </summary>
public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options) : DbContext(options), IOutboxDbContext
{
    /// <summary>Schema posseduto dal servizio; ospita anche la tabella di storia delle migrazioni.</summary>
    public const string SchemaName = "ingestion";

    public const string UploadsUniqueIndexName = "ux_uploads_user_source_external";

    public DbSet<Upload> Uploads => Set<Upload>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    /// <summary>
    /// Configurazione condivisa da host e factory di progettazione: PostgreSQL, nomi in snake_case e tabella di storia
    /// delle migrazioni nello schema del servizio (esplicita: non dipende dal <c>search_path</c> del ruolo).
    /// Nessun logging dei dati sensibili (REQ-034).
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", SchemaName))
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<Upload>(entity =>
        {
            entity.ToTable("uploads");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedNever();
            entity.Property(u => u.UserId).IsRequired();
            entity.Property(u => u.Source).IsRequired();
            entity.Property(u => u.ExternalId).IsRequired();
            entity.Property(u => u.FileName).IsRequired();
            entity.Property(u => u.SizeBytes).IsRequired();
            entity.Property(u => u.CreatedAt).IsRequired();

            // Deduplicazione (REQ-011): una sola riga per utente, fonte e identificativo nella fonte.
            entity.HasIndex(u => new { u.UserId, u.Source, u.ExternalId })
                .IsUnique()
                .HasDatabaseName(UploadsUniqueIndexName);
        });

        modelBuilder.ApplyOutbox();
    }
}
