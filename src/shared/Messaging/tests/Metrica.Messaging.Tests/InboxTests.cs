using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Metrica.TestInfrastructure;
using Xunit;

namespace Metrica.Messaging.Tests;

/// <summary>Forma della tabella <c>ingestion.inbox</c> (guardia per la migrazione di T11) e opzioni del consumatore.</summary>
[Collection(NatsCollection.Name)]
[Trait("Category", "Consumer")]
public sealed class InboxTests(TimescaleFixture db) : IAsyncLifetime
{
    private string ServiceConnection => db.ConnectionStringFor("ingestion");

    public Task InitializeAsync() => TestDbContext.RecreateTablesAsync(ServiceConnection);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InboxTable_HasExactlyTheDocumentedShape()
    {
        var columns = new Dictionary<string, (string Type, string Nullable, string? Default)>();
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'ingestion' AND table_name = 'inbox'
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }

        var expected = new Dictionary<string, (string Type, string Nullable, string? Default)>
        {
            ["message_id"] = ("uuid", "NO", null),
            ["consumer"] = ("text", "NO", null),
            ["processed_at"] = ("timestamp with time zone", "NO", null),
        };
        Assert.Equal(expected.OrderBy(p => p.Key), columns.OrderBy(p => p.Key));

        // Chiave primaria composta, nell'ordine (consumer, message_id).
        var primaryKey = new List<string>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT a.attname
            FROM pg_index i
            CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS k(attnum, ord)
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
            WHERE i.indrelid = 'ingestion.inbox'::regclass AND i.indisprimary
            ORDER BY k.ord
            """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                primaryKey.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["consumer", "message_id"], primaryKey);

        // Un solo vincolo (la PK) e nessun altro indice: l'unicita' e' garantita dal database.
        await using var constraints = new NpgsqlCommand(
            "SELECT string_agg(contype::text, ',' ORDER BY contype) FROM pg_constraint WHERE conrelid = 'ingestion.inbox'::regclass",
            connection);
        Assert.Equal("p", (string?)await constraints.ExecuteScalarAsync());
        await using var indexes = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'ingestion' AND tablename = 'inbox'", connection);
        Assert.Equal(1L, (long)(await indexes.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task InboxTable_RejectsTheSameConsumerAndMessageTwice_ButNotTheSameMessageForAnotherConsumer()
    {
        var id = Guid.NewGuid();
        await using var context = new TestDbContext(TestDbContext.OptionsFor(ServiceConnection));
        context.Inbox.Add(new InboxMessage { MessageId = id, Consumer = "a", ProcessedAt = DateTimeOffset.UtcNow });
        context.Inbox.Add(new InboxMessage { MessageId = id, Consumer = "b", ProcessedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        await using var second = new TestDbContext(TestDbContext.OptionsFor(ServiceConnection));
        second.Inbox.Add(new InboxMessage { MessageId = id, Consumer = "a", ProcessedAt = DateTimeOffset.UtcNow });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public void ApplyInbox_FollowsTheDefaultSchemaOfTheContext()
    {
        using var context = new TestDbContext(TestDbContext.OptionsFor(ServiceConnection));
        using var other = new OtherSchemaContext(new DbContextOptionsBuilder<OtherSchemaContext>().UseNpgsql(ServiceConnection).Options);

        var entity = context.Model.FindEntityType(typeof(InboxMessage))!;
        var otherEntity = other.Model.FindEntityType(typeof(InboxMessage))!;

        Assert.Equal("inbox", entity.GetTableName());
        Assert.Equal("ingestion", entity.GetSchema());
        Assert.Equal("inbox", otherEntity.GetTableName());
        Assert.Equal("altro_schema", otherEntity.GetSchema());
    }

    private sealed class OtherSchemaContext(DbContextOptions<OtherSchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("altro_schema");
            modelBuilder.ApplyInbox();
        }
    }

    [Fact]
    public void ConsumerOptions_AcceptsDefaultsWithRequiredNames()
    {
        var options = new ConsumerOptions { FilterSubject = "ingestion.activity.imported", DurableName = "activities-activity-imported" };

        options.Validate();

        Assert.Equal("EVENTS", options.Stream);
        Assert.Equal(5, options.MaxDeliver);
        Assert.Equal(TimeSpan.FromSeconds(60), options.AckWait);
        Assert.Equal(1, options.MaxConcurrency);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], options.Backoff);
    }

    [Theory]
    [InlineData("", "ingestion.x")]
    [InlineData("  ", "ingestion.x")]
    [InlineData("with.dot", "ingestion.x")]
    [InlineData("with space", "ingestion.x")]
    [InlineData("star*", "ingestion.x")]
    [InlineData("gt>", "ingestion.x")]
    [InlineData("ok", "")]
    [InlineData("ok", "  ")]
    public void ConsumerOptions_RejectsInvalidNames(string durable, string subject)
    {
        var options = new ConsumerOptions { FilterSubject = subject, DurableName = durable };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void ConsumerOptions_RejectsInvalidNumbers()
    {
        var valid = new ConsumerOptions { FilterSubject = "ingestion.x", DurableName = "ok" };

        Assert.Throws<ArgumentException>((valid with { MaxDeliver = 0 }).Validate);
        Assert.Throws<ArgumentException>((valid with { MaxConcurrency = 0 }).Validate);
        Assert.Throws<ArgumentException>((valid with { AckWait = TimeSpan.Zero }).Validate);
        Assert.Throws<ArgumentException>((valid with { Backoff = [] }).Validate);
        Assert.Throws<ArgumentException>((valid with { Backoff = [TimeSpan.Zero] }).Validate);
        Assert.Throws<ArgumentException>((valid with { Stream = " " }).Validate);
    }

    [Fact]
    public void AddEventConsumer_ValidatesOptionsAtRegistration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddEventConsumer<TestDbContext, TestPayload, TestHandler>(
            new ConsumerOptions { FilterSubject = "ingestion.x", DurableName = "a.b" }));
        Assert.Throws<ArgumentNullException>(() => services.AddEventConsumer<TestDbContext, TestPayload, TestHandler>(null!));
    }
}
