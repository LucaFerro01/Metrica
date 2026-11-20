using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Metrica.Messaging.Tests;

/// <summary>Entita' di dominio di prova: la colonna <c>Code</c> ha un vincolo di unicita' per provocare un fallimento di <c>SaveChanges</c>.</summary>
public sealed class TestItem
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>Payload di prova della busta.</summary>
public sealed record TestPayload(string Name, int Count);

/// <summary>
/// Contesto di prova configurato come i servizi: schema predefinito <c>ingestion</c>, snake_case, <c>ApplyOutbox</c>
/// e <c>ApplyInbox</c>.
/// </summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IOutboxDbContext, IInboxDbContext
{
    public DbSet<TestItem> Items => Set<TestItem>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    public static DbContextOptions<TestDbContext> OptionsFor(string connectionString) =>
        new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ingestion");
        modelBuilder.Entity<TestItem>(e =>
        {
            e.ToTable("test_items");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.ApplyOutbox();
        modelBuilder.ApplyInbox();
    }

    /// <summary>
    /// Ricrea le tabelle di prova con lo script generato dal modello, eseguito come proprietario dello schema
    /// (<c>ingestion_svc</c>): ogni test parte da tabelle vuote.
    /// </summary>
    public static async Task RecreateTablesAsync(string ownerConnectionString)
    {
        await using var context = new TestDbContext(OptionsFor(ownerConnectionString));
        var script = context.Database.GenerateCreateScript();

        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "DROP TABLE IF EXISTS ingestion.outbox; DROP TABLE IF EXISTS ingestion.inbox; DROP TABLE IF EXISTS ingestion.test_items;"
            // Tabella fuori dal modello con un vincolo di unicita' DIFFERITO: la violazione emerge solo al COMMIT
            // (serve a provare che l'ack non precede il commit).
            + " DROP TABLE IF EXISTS ingestion.test_deferred;"
            + " CREATE TABLE ingestion.test_deferred (code text NOT NULL, CONSTRAINT uq_test_deferred UNIQUE (code) DEFERRABLE INITIALLY DEFERRED);"
            + script,
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
