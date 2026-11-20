using Microsoft.EntityFrameworkCore;
using Metrica.Messaging;

namespace Metrica.Activities;

/// <summary>
/// Database del servizio (schema <c>activities</c>, ruolo <c>activities_svc</c>): attivita', giri, stream dei punti
/// (hypertable) e inbox del consumatore di eventi. I nomi di tabelle e colonne sono in snake_case (convenzione impostata
/// alla registrazione del contesto).
/// </summary>
public sealed class ActivitiesDbContext(DbContextOptions<ActivitiesDbContext> options) : DbContext(options), IInboxDbContext
{
    /// <summary>Schema posseduto dal servizio; ospita anche la tabella di storia delle migrazioni.</summary>
    public const string SchemaName = "activities";

    public const string ActivitiesUniqueIndexName = "ux_activities_user_source_external";

    public const string ActivitiesUserStartIndexName = "ix_activities_user_start";

    public const string StreamActivityTimeIndexName = "ix_activity_stream_activity_time";

    public DbSet<Activity> Activities => Set<Activity>();

    public DbSet<ActivityLap> Laps => Set<ActivityLap>();

    /// <summary>Punti dello stream: sola lettura (T13). La scrittura passa da <see cref="StreamWriter"/>.</summary>
    public DbSet<ActivityStreamPoint> StreamPoints => Set<ActivityStreamPoint>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

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

        modelBuilder.Entity<Activity>(entity =>
        {
            entity.ToTable("activities");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).ValueGeneratedNever();
            entity.Property(a => a.UserId).IsRequired();
            entity.Property(a => a.Source).IsRequired();
            entity.Property(a => a.ExternalId).IsRequired();
            entity.Property(a => a.Sport).HasConversion<SportConverter>().HasColumnType("text").IsRequired();
            entity.Property(a => a.StartTime).IsRequired();
            entity.Property(a => a.ElapsedSeconds).IsRequired();
            entity.Property(a => a.MovingSeconds).IsRequired();
            entity.Property(a => a.DistanceMeters).IsRequired();
            entity.Property(a => a.CreatedAt).IsRequired();

            // Deduplicazione (REQ-011): una sola attivita' per utente, fonte e identificativo nella fonte.
            entity.HasIndex(a => new { a.UserId, a.Source, a.ExternalId })
                .IsUnique()
                .HasDatabaseName(ActivitiesUniqueIndexName);

            // Elenco dell'utente dal piu' recente (T13).
            entity.HasIndex(a => new { a.UserId, a.StartTime })
                .IsDescending(false, true)
                .HasDatabaseName(ActivitiesUserStartIndexName);
        });

        modelBuilder.Entity<ActivityLap>(entity =>
        {
            entity.ToTable("activity_laps");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).ValueGeneratedNever();
            entity.Property(l => l.ActivityId).IsRequired();
            entity.Property(l => l.StartTime).IsRequired();
            entity.Property(l => l.ElapsedSeconds).IsRequired();
            entity.Property(l => l.DistanceMeters).IsRequired();

            entity.HasOne<Activity>()
                .WithMany()
                .HasForeignKey(l => l.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Hypertable: senza chiave primaria; la trasformazione in hypertable e' un passo SQL della migrazione.
        modelBuilder.Entity<ActivityStreamPoint>(entity =>
        {
            entity.ToTable("activity_stream");
            entity.HasNoKey();
            entity.Property(p => p.ActivityId).IsRequired();
            entity.Property(p => p.Time).IsRequired();
            entity.Property(p => p.SpeedMps).HasColumnName("speed_mps");

            entity.HasIndex(p => new { p.ActivityId, p.Time }).HasDatabaseName(StreamActivityTimeIndexName);
        });

        modelBuilder.ApplyInbox();
    }
}
