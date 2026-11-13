using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;
using Xunit;

namespace TriMetric.Messaging.Tests;

/// <summary>Outbox transazionale e forma della tabella <c>ingestion.outbox</c> (REQ-012, DEC-001).</summary>
[Trait("Category", "Outbox")]
public sealed class OutboxTests(TimescaleFixture db) : IClassFixture<TimescaleFixture>, IAsyncLifetime
{
    private const string Subject = "ingestion.activity.imported";

    private string ServiceConnection => db.ConnectionStringFor("ingestion");

    public Task InitializeAsync() => TestDbContext.RecreateTablesAsync(ServiceConnection);

    public Task DisposeAsync() => Task.CompletedTask;

    private static EventEnvelope<TestPayload> NewEnvelope() => new(
        Id: Guid.NewGuid(),
        Type: EventNames.ActivityImported,
        Version: 1,
        OccurredAt: new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero),
        UserId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        Data: new TestPayload("corsa", 42));

    private TestDbContext NewContext() => new(TestDbContext.OptionsFor(ServiceConnection));

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(o => o.UseNpgsql(ServiceConnection).UseSnakeCaseNamingConvention());
        services.AddOutbox<TestDbContext>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM ingestion.{table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AddAndSaveChanges_WritesDomainRowAndOutboxRowInSameTransaction()
    {
        var envelope = NewEnvelope();
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            context.Items.Add(new TestItem { Id = Guid.NewGuid(), Code = "A" });
            outbox.Add(Subject, envelope);

            await context.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var item = await verify.Items.SingleAsync();
        var row = await verify.Outbox.SingleAsync();
        Assert.Equal("A", item.Code);
        Assert.Equal(envelope.Id, row.Id);
        Assert.Equal(Subject, row.Subject);
        Assert.Equal(envelope.Type, row.Type);
        Assert.Null(row.PublishedAt);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LastError);
        Assert.True(DateTimeOffset.UtcNow - row.CreatedAt < TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.Zero, row.CreatedAt.Offset);

        var roundTrip = JsonSerializer.Deserialize<EventEnvelope<TestPayload>>(row.Payload, ContractJson.Options);
        Assert.Equal(envelope, roundTrip);
    }

    [Fact]
    public async Task Payload_IsUtf8JsonWithContractOptions()
    {
        var envelope = NewEnvelope();
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            scope.ServiceProvider.GetRequiredService<IOutbox>().Add(Subject, envelope);
            await context.SaveChangesAsync();
        }

        await using var verify = NewContext();
        var row = await verify.Outbox.SingleAsync();
        using var json = JsonDocument.Parse(row.Payload);
        Assert.Equal(envelope.Id, json.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("ActivityImported", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("corsa", json.RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task SaveChangesFailure_LeavesNoOutboxRow()
    {
        await using (var seed = NewContext())
        {
            seed.Items.Add(new TestItem { Id = Guid.NewGuid(), Code = "DUP" });
            await seed.SaveChangesAsync();
        }

        var envelope = NewEnvelope();
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            context.Items.Add(new TestItem { Id = Guid.NewGuid(), Code = "DUP" });
            scope.ServiceProvider.GetRequiredService<IOutbox>().Add(Subject, envelope);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
        }

        Assert.Equal(0, await CountAsync("outbox"));
        Assert.Equal(1, await CountAsync("test_items"));
    }

    [Fact]
    public async Task AddWithoutSaveChanges_WritesNothing()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        scope.ServiceProvider.GetRequiredService<IOutbox>().Add(Subject, NewEnvelope());

        Assert.Equal(0, await CountAsync("outbox"));
        Assert.Single(context.ChangeTracker.Entries<OutboxMessage>(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task DuplicateId_FailsWithPrimaryKeyViolation()
    {
        var envelope = NewEnvelope();
        await using var provider = BuildProvider();
        await using (var first = provider.CreateAsyncScope())
        {
            first.ServiceProvider.GetRequiredService<IOutbox>().Add(Subject, envelope);
            await first.ServiceProvider.GetRequiredService<TestDbContext>().SaveChangesAsync();
        }

        await using var second = provider.CreateAsyncScope();
        var context = second.ServiceProvider.GetRequiredService<TestDbContext>();
        second.ServiceProvider.GetRequiredService<IOutbox>().Add(Subject, envelope);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)ex.InnerException!).SqlState);
        Assert.Equal(1, await CountAsync("outbox"));
    }

    [Fact]
    public async Task DuplicateId_InSameContext_IsRejectedBeforeAnyWrite()
    {
        var envelope = NewEnvelope();
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        outbox.Add(Subject, envelope);

        Assert.Throws<InvalidOperationException>(() => outbox.Add(Subject, envelope));
        Assert.Equal(0, await CountAsync("outbox"));
    }

    [Theory]
    [InlineData("ingestion.activity.imported")]
    [InlineData("identity.user.registered")]
    [InlineData("activities.activity.stored")]
    [InlineData("analytics.metrics.updated")]
    [InlineData("training-plan.plan.changed")]
    public async Task Add_AcceptsSubjectsOfTheEventsStream(string subject)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IOutbox>().Add(subject, NewEnvelope());

        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Single(context.ChangeTracker.Entries<OutboxMessage>());
    }

    [Theory]
    [InlineData("dlq.x")]
    [InlineData("other.thing.happened")]
    [InlineData("ingestion")]
    [InlineData("ingestion.")]
    [InlineData("ingestionx.thing")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Add_RejectsSubjectOutsideTheEventsStream(string subject)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        var ex = Assert.ThrowsAny<ArgumentException>(() => outbox.Add(subject, NewEnvelope()));

        Assert.Equal("subject", ex.ParamName);
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Empty(context.ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public async Task Add_RejectsEmptyEnvelopeId()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        var ex = Assert.Throws<ArgumentException>(() => outbox.Add(Subject, NewEnvelope() with { Id = Guid.Empty }));

        Assert.Equal("envelope", ex.ParamName);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<TestDbContext>().ChangeTracker.Entries<OutboxMessage>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Add_RejectsEmptyEnvelopeType(string type)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        var ex = Assert.Throws<ArgumentException>(() => outbox.Add(Subject, NewEnvelope() with { Type = type }));

        Assert.Equal("envelope", ex.ParamName);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<TestDbContext>().ChangeTracker.Entries<OutboxMessage>());
    }

    [Fact]
    public async Task OutboxTable_HasExactlyTheDocumentedShape()
    {
        var columns = new Dictionary<string, (string Type, string Nullable, string? Default)>();
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(
            """
            SELECT column_name, data_type, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'ingestion' AND table_name = 'outbox'
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
            ["id"] = ("uuid", "NO", null),
            ["subject"] = ("text", "NO", null),
            ["type"] = ("text", "NO", null),
            ["payload"] = ("bytea", "NO", null),
            ["created_at"] = ("timestamp with time zone", "NO", null),
            ["published_at"] = ("timestamp with time zone", "YES", null),
            ["attempts"] = ("integer", "NO", "0"),
            ["last_error"] = ("text", "YES", null),
        };
        Assert.Equal(expected.OrderBy(p => p.Key), columns.OrderBy(p => p.Key));

        await using var primaryKey = new NpgsqlCommand(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'ingestion.outbox'::regclass AND i.indisprimary
            """,
            connection);
        Assert.Equal("id", (string?)await primaryKey.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OutboxTable_HasPartialIndexOnUnpublishedRows()
    {
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname = 'ingestion' AND tablename = 'outbox' AND indexname = 'ix_outbox_unpublished'",
            connection);

        var definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("(created_at)", definition);
        Assert.Contains("WHERE (published_at IS NULL)", definition);
        Assert.DoesNotContain("UNIQUE", definition);
    }
}
