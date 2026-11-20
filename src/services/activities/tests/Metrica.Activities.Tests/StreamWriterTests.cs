using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Metrica.Contracts;
using Metrica.Messaging;
using Metrica.TestInfrastructure;
using Xunit.Abstractions;

namespace Metrica.Activities.Tests;

/// <summary>
/// <see cref="StreamWriter"/>: COPY binario dei punti nella hypertable, con la connessione e la transazione correnti del
/// contesto (REQ-016, DEC-003). Ogni test usa identificativi propri (<c>activity_id</c> nuovo): nessuna pulizia necessaria.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "StreamWriter")]
[Trait("Category", "Persistence")]   // il filtro del piano (Category=Persistence) deve eseguire anche il COPY
public sealed class StreamWriterTests(TimescaleFixture db, ITestOutputHelper output)
{
    private static readonly TimeSpan SaveBudget = TimeSpan.FromSeconds(10);

    private async Task<ActivitiesDbContext> NewMigratedContextAsync()
    {
        await MigratedDatabase.EnsureAsync(db);
        return MigratedDatabase.NewContext(db);
    }

    // ---- Volume e correttezza -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Writes50000Points_WithAllColumns_InUnderTenSeconds_AndEveryRowMatches()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(50_000), StreamTestData.StartWithFraction);

        var watch = Stopwatch.StartNew();
        long written;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            written = await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        watch.Stop();
        output.WriteLine($"COPY di 50.000 punti x 8 colonne + commit: {watch.Elapsed.TotalMilliseconds:0} ms");

        Assert.Equal(50_000, written);
        Assert.True(watch.Elapsed < SaveBudget, $"50.000 punti salvati in {watch.Elapsed.TotalSeconds:0.00} s (tetto {SaveBudget.TotalSeconds:0} s).");
        var rows = await StreamTestData.ReadAsync(db, id);
        StreamTestData.AssertEqual(start, stream, id, rows);
        // Estremi esplicitati (oltre al confronto di tutte le righe): primo e ultimo punto.
        Assert.Equal(start.UtcTicks, rows[0].Time.Ticks);
        Assert.Equal(start.UtcTicks + StreamTestData.OffsetMilliseconds(49_999) * TimeSpan.TicksPerMillisecond, rows[^1].Time.Ticks);
    }

    [Fact]
    public async Task Writes100000Points_WithAllEightColumns_AndEveryRowMatches_TimingForInformation()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(StreamLimits.MaxPoints, nullsInsideColumns: false), StreamTestData.StartWithFraction);

        var watch = Stopwatch.StartNew();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(StreamLimits.MaxPoints, await writer.WriteAsync(id, start, stream));
            await transaction.CommitAsync();
        }

        watch.Stop();
        output.WriteLine($"COPY di 100.000 punti x 8 colonne + commit: {watch.Elapsed.TotalMilliseconds:0} ms");

        // Soglia molto larga: il valore misurato e' riportato, qui si intercettano solo degenerazioni (ordini di grandezza).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"100.000 punti in {watch.Elapsed.TotalSeconds:0.00} s.");
        var rows = await StreamTestData.ReadAsync(db, id);
        StreamTestData.AssertEqual(start, stream, id, rows);
    }

    [Fact]
    public async Task NullColumns_StayNull_WholeNullColumnAndNullsInsideAColumn()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        // Altitudine e cadenza del tutto assenti (null); FC, latitudine e longitudine con buchi dentro la colonna.
        var stream = StreamTestData.Build(2_000, allColumns: false);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        var rows = await StreamTestData.ReadAsync(db, id);
        StreamTestData.AssertEqual(start, stream, id, rows);
        Assert.All(rows, r => Assert.Null(r.Altitude));
        Assert.All(rows, r => Assert.Null(r.Cadence));
        Assert.Contains(rows, r => r.HeartRate is null);
        Assert.Contains(rows, r => r.HeartRate is not null);
        Assert.Contains(rows, r => r.Latitude is null && r.Longitude is null);
        Assert.All(rows, r => Assert.NotNull(r.Power));
    }

    [Fact]
    public async Task AllColumnsNull_ExceptTime_WritesRowsWithOnlyActivityAndTime()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = new StreamColumns([0, 1, 2], null, null, null, null, null, null, null);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(3, await writer.WriteAsync(id, start, stream));
            await transaction.CommitAsync();
        }

        var rows = await StreamTestData.ReadAsync(db, id);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(id, r.ActivityId);
            Assert.Null(r.Latitude);
            Assert.Null(r.Longitude);
            Assert.Null(r.Altitude);
            Assert.Null(r.HeartRate);
            Assert.Null(r.Power);
            Assert.Null(r.Cadence);
            Assert.Null(r.Speed);
        });
    }

    [Fact]
    public async Task EachColumn_LandsInItsOwnDatabaseColumn_NoneSwapped()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        // Valori distinti per colonna: uno scambio (ad esempio cadenza al posto di potenza) non puo' passare inosservato.
        var stream = new StreamColumns([5.0], [45.1234567], [-7.7654321], [1234.5], [151], [260], [88], [3.141]);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        var row = Assert.Single(await StreamTestData.ReadAsync(db, id));
        Assert.Equal(45.1234567, row.Latitude);
        Assert.Equal(-7.7654321, row.Longitude);
        Assert.Equal(1234.5, row.Altitude);
        Assert.Equal(151, row.HeartRate);
        Assert.Equal(260, row.Power);
        Assert.Equal(88, row.Cadence);
        Assert.Equal(3.141, row.Speed);
    }

    // ---- Calcolo del tempo ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Time_IsStartPlusOffset_Exactly_WithMillisecondAndSubMillisecondOffsets()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        // Offset tipici dopo l'arrotondamento a 0,001 s (che in binario non sono esatti: 0,001, 12,345, 99999,999...)
        // e uno sotto il millisecondo (0,0015 s = 1500 us, la risoluzione di timestamptz).
        double[] offsets = [0.0, 0.001, 0.0015, 0.002, 0.3, 1.1, 12.345, 59.999, 3600.5, 86399.999, 99999.999];
        long[] expectedMicroseconds = [0, 1000, 1500, 2000, 300_000, 1_100_000, 12_345_000, 59_999_000, 3_600_500_000, 86_399_999_000, 99_999_999_000];
        var stream = new StreamColumns(offsets, null, null, null, null, null, null, null);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        var rows = await StreamTestData.ReadAsync(db, id);
        Assert.Equal(offsets.Length, rows.Count);
        for (var i = 0; i < offsets.Length; i++)
        {
            Assert.Equal(start.UtcTicks + expectedMicroseconds[i] * 10, rows[i].Time.Ticks);
        }

        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.Time.Kind));
    }

    [Fact]
    public async Task Time_IsNotDriftedByFloatingPointAccumulation_OverManyPoints()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        // 100.000 punti a passo di 0,001 s: la deriva di una somma in virgola mobile sarebbe visibile al microsecondo.
        var offsets = Enumerable.Range(0, 100_000).Select(i => i / 1000.0).ToArray();
        var stream = new StreamColumns(offsets, null, null, null, null, null, null, null);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        var rows = await StreamTestData.ReadAsync(db, id);
        Assert.Equal(100_000, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Time.Ticks != start.UtcTicks + i * TimeSpan.TicksPerMillisecond)
            {
                Assert.Fail($"Punto {i}: tempo {rows[i].Time.Ticks - start.UtcTicks} tick dopo l'inizio, atteso {i * TimeSpan.TicksPerMillisecond}.");
            }
        }
    }

    [Fact]
    public async Task Time_UsesTheUtcInstant_WhenTheStartHasANonZeroOffset()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var id = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2));   // = 10:00:00 UTC
        var stream = new StreamColumns([0, 90.5], null, null, null, null, null, null, null);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        var rows = await StreamTestData.ReadAsync(db, id);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), rows[0].Time);
        Assert.Equal(new DateTime(2026, 3, 1, 10, 1, 30, 500, DateTimeKind.Utc), rows[1].Time);
    }

    [Fact]
    public async Task EqualOffsets_KeepBothPoints_NoDeduplication()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = new StreamColumns([1, 1, 2], null, null, null, [100, 101, 102], null, null, null);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(3, await writer.WriteAsync(id, start, stream));
            await transaction.CommitAsync();
        }

        Assert.Equal(3, await StreamTestData.CountAsync(db, id));
    }

    // ---- Casi limite ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SinglePoint_IsWritten()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = new StreamColumns([0], [45.0], [7.0], [300.0], [120], [200], [80], [2.5]);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(1, await writer.WriteAsync(id, start, stream));
            await transaction.CommitAsync();
        }

        var row = Assert.Single(await StreamTestData.ReadAsync(db, id));
        Assert.Equal(start.UtcTicks, row.Time.Ticks);
        Assert.Equal(120, row.HeartRate);
    }

    [Fact]
    public async Task EmptyStream_WritesZeroRows_WithoutError_AndLeavesTheTransactionUsable()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = new StreamColumns([], null, null, null, null, null, null, null);
        var emptyColumns = new StreamColumns([], [], [], [], [], [], [], []);
        var consumer = $"t11-{Guid.NewGuid():N}";

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(0, await writer.WriteAsync(id, start, stream));
            Assert.Equal(0, await writer.WriteAsync(id, start, emptyColumns));
            // La transazione e' ancora valida: una scrittura successiva ha successo.
            context.Inbox.Add(new InboxMessage { Consumer = consumer, MessageId = Guid.NewGuid(), ProcessedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
        Assert.Equal(1, await StreamTestData.CountInboxAsync(db, consumer));
    }

    // ---- Transazione: la stessa del contesto ---------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_LeavesNoPoints_AndUncommittedPointsAreInvisibleToOtherConnections()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(5_000), StreamTestData.StartWithFraction);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.Equal(5_000, await writer.WriteAsync(id, start, stream));

            // Una connessione diversa non vede i punti: sono dentro la transazione del contesto, non gia' confermati.
            Assert.Equal(0, await StreamTestData.CountAsync(db, id));

            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task Commit_MakesThePointsVisible()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(5_000), StreamTestData.StartWithFraction);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            Assert.Equal(0, await StreamTestData.CountAsync(db, id));
            await transaction.CommitAsync();
        }

        Assert.Equal(5_000, await StreamTestData.CountAsync(db, id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PointsAndAnotherWriteInTheSameTransaction_ArePersistedOrDiscardedTogether(bool commit)
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(1_000), StreamTestData.StartWithFraction);
        var consumer = $"t11-{Guid.NewGuid():N}";

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            // Come in T12: inbox (e gestore) e punti nella stessa transazione.
            context.Inbox.Add(new InboxMessage { Consumer = consumer, MessageId = Guid.NewGuid(), ProcessedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await writer.WriteAsync(id, start, stream);

            if (commit)
            {
                await transaction.CommitAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }
        }

        var expected = commit ? 1 : 0;
        Assert.Equal(expected, await StreamTestData.CountInboxAsync(db, consumer));
        Assert.Equal(expected * 1_000, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task AFailureAfterTheCopy_RollsBackThePoints_WithTheSameTransaction()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(1_000), StreamTestData.StartWithFraction);
        var consumer = $"t11-{Guid.NewGuid():N}";
        var messageId = Guid.NewGuid();

        // Una riga di inbox per (consumer, messageId) gia' presente: la seconda SaveChanges viola la chiave primaria.
        await using (var seed = MigratedDatabase.NewContext(db))
        {
            seed.Inbox.Add(new InboxMessage { Consumer = consumer, MessageId = messageId, ProcessedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            context.Inbox.Add(new InboxMessage { Consumer = consumer, MessageId = messageId, ProcessedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task WithoutAnActiveTransaction_Throws_WritesNothing_AndDoesNotOpenAConnection()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(100), StreamTestData.StartWithFraction);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(id, start, stream));

        Assert.Contains("transazione", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);   // nessuna connessione propria
        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task WithoutAnActiveTransaction_EvenWithAnOpenConnection_Throws_AndWritesNothing()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(100), StreamTestData.StartWithFraction);
        await context.Database.OpenConnectionAsync();   // connessione aperta ma nessuna transazione (autocommit)

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(id, start, stream));

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task AfterCommit_TheTransactionIsOver_AndANewWriteThrows()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(100), StreamTestData.StartWithFraction);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(id, start, stream);
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(id, start, stream));

        Assert.Equal(100, await StreamTestData.CountAsync(db, id));   // solo la prima scrittura
    }

    [Fact]
    public async Task WithAClosedConnectionInsideATransaction_ThrowsAClearError_AndWritesNothing()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(100), StreamTestData.StartWithFraction);
        var transaction = await context.Database.BeginTransactionAsync();
        try
        {
            context.Database.GetDbConnection().Close();   // la transazione server-side e' persa con la connessione

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(id, start, stream));

            Assert.Contains("connessione", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NpgsqlException)
            {
                // La transazione era gia' persa: la pulizia puo' fallire, non e' oggetto del test.
            }
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    // ---- Validazione ---------------------------------------------------------------------------------------------------

    public static TheoryData<string> MisalignedColumns() => new()
    {
        "latitude-short", "longitude-long", "altitude-short", "heartrate-short", "power-long", "cadence-short", "speed-long",
        "time-longer-than-columns",
    };

    [Theory]
    [MemberData(nameof(MisalignedColumns))]
    public async Task ColumnsOfDifferentLength_ThrowArgumentException_AndNothingIsWritten_TransactionStaysUsable(string which)
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = Misaligned(which);
        var consumer = $"t11-{Guid.NewGuid():N}";

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(id, start, stream));

            Assert.Equal("stream", error.ParamName);
            // La validazione avviene prima del COPY: la transazione e' intatta e puo' ancora essere confermata.
            context.Inbox.Add(new InboxMessage { Consumer = consumer, MessageId = Guid.NewGuid(), ProcessedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
        Assert.Equal(1, await StreamTestData.CountInboxAsync(db, consumer));
    }

    [Fact]
    public async Task ColumnLengthError_DoesNotLeakValuesInTheMessage()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var stream = new StreamColumns([0, 1, 2], [45.123456789, 45.2], null, [177.5, 178.5, 179.5], null, null, null, null);
        await using var transaction = await context.Database.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, stream));

        Assert.DoesNotContain("45.123", error.Message);   // REQ-034: nessuna coordinata o valore nei messaggi
        Assert.DoesNotContain("177", error.Message);
        Assert.Contains("Latitude", error.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e18)]
    [InlineData(-1e18)]
    public async Task NonFiniteOrOutOfRangeOffsets_ThrowArgumentException_BeforeAnyWrite(double badOffset)
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var stream = new StreamColumns([0, 1, badOffset, 3], null, null, null, null, null, null, null);
        await using var transaction = await context.Database.BeginTransactionAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(id, start, stream));

        // La transazione e' ancora utilizzabile (nessun COPY avviato e interrotto).
        await context.Database.ExecuteSqlRawAsync("SELECT 1");
        await transaction.RollbackAsync();
        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task NullStream_ThrowsArgumentNullException()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        await using var transaction = await context.Database.BeginTransactionAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.WriteAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, null!));
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsOperationCanceled_AndWritesNothing()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(100), StreamTestData.StartWithFraction);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(id, start, stream, cancelled.Token));

        await transaction.RollbackAsync();
        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    /// <summary>Colonna che annulla il token quando il COPY legge il punto <paramref name="cancelAt"/>: annullamento deterministico a meta' COPY.</summary>
    private sealed class CancellingColumn(double?[] values, CancellationTokenSource source, int cancelAt) : IReadOnlyList<double?>
    {
        public int Count => values.Length;

        public double? this[int index]
        {
            get
            {
                if (index == cancelAt)
                {
                    source.Cancel();
                }

                return values[index];
            }
        }

        public IEnumerator<double?> GetEnumerator() => ((IEnumerable<double?>)values).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task CancellationDuringTheCopy_ThrowsOperationCanceled_RollsBack_AndTheContextStaysUsable()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, start) = (Guid.NewGuid(), StreamTestData.StartWithFraction);
        var full = StreamTestData.Build(StreamLimits.MaxPoints, nullsInsideColumns: false);
        using var source = new CancellationTokenSource();
        var stream = full with { Latitude = new CancellingColumn([.. full.Latitude!], source, cancelAt: 1_000) };

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(id, start, stream, source.Token));
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
        // Il contesto e la sua connessione sono riutilizzabili per una nuova transazione.
        await using var next = await context.Database.BeginTransactionAsync();
        Assert.Equal(StreamLimits.MaxPoints, await writer.WriteAsync(id, start, full));
        await next.CommitAsync();
        Assert.Equal(StreamLimits.MaxPoints, await StreamTestData.CountAsync(db, id));
    }

    [Fact]
    public async Task ConnectionClosedAndReopenedDirectly_WithAStaleEfTransaction_Throws_AndWritesNothing()
    {
        await using var context = await NewMigratedContextAsync();
        var writer = new StreamWriter(context);
        var (id, stream, start) = (Guid.NewGuid(), StreamTestData.Build(10), StreamTestData.StartWithFraction);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var connection = context.Database.GetDbConnection();

        // Chiusura e riapertura diretta della DbConnection (non con l'API di EF): CurrentTransaction resta valorizzata e lo
        // stato e' Open, ma la transazione e' ormai scollegata: un COPY andrebbe in autocommit.
        connection.Close();
        await connection.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(id, start, stream));

        Assert.Equal(0, await StreamTestData.CountAsync(db, id));
    }

    // ---- Coerenza con il modello EF e con lo schema reale --------------------------------------------------------------

    [Fact]
    public async Task CopyColumns_AreExactlyTheColumnsOfTheEfModel()
    {
        await using var context = await NewMigratedContextAsync();
        var entity = context.Model.FindEntityType(typeof(ActivityStreamPoint))!;
        var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());

        // Il COPY nomina le colonne: l'ordine fisico della tabella non conta, conta che l'insieme coincida (e che per ogni
        // proprieta' del modello esista una colonna COPY con lo stesso nome, e viceversa).
        var modelColumns = entity.GetProperties().Select(p => p.GetColumnName(table)!).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.Equal("activity_stream", entity.GetTableName());
        Assert.Equal("activities", entity.GetSchema());
        Assert.Equal(modelColumns, StreamWriter.Columns.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(StreamWriter.TableName, $"{entity.GetSchema()}.{entity.GetTableName()}");
    }

    [Fact]
    public async Task CopyColumns_ExistInTheRealSchema_WithAMatchingType()
    {
        await MigratedDatabase.EnsureAsync(db);
        await using var connection = new NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT column_name, data_type FROM information_schema.columns WHERE table_schema = 'activities' AND table_name = 'activity_stream'",
            connection);
        var real = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                real[reader.GetString(0)] = reader.GetString(1);
            }
        }

        Assert.Equal(
            real.Keys.OrderBy(k => k, StringComparer.Ordinal),
            StreamWriter.Columns.Select(c => c.Name).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var column in StreamWriter.Columns)
        {
            var expectedType = column.DbType switch
            {
                NpgsqlDbType.Uuid => "uuid",
                NpgsqlDbType.TimestampTz => "timestamp with time zone",
                NpgsqlDbType.Double => "double precision",
                _ => throw new InvalidOperationException($"Tipo non previsto per {column.Name}: {column.DbType}."),
            };
            Assert.Equal(expectedType, real[column.Name]);
        }
    }

    [Fact]
    public void CopyColumns_MapEveryStreamColumnOfTheContractExactlyOnce()
    {
        // Le 7 colonne di valori del contratto (oltre a tempo) hanno ciascuna una colonna COPY distinta.
        var names = StreamWriter.Columns.Select(c => c.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(
            ["activity_id", "time", "latitude", "longitude", "altitude_meters", "heart_rate", "power_watts", "cadence", "speed_mps"],
            names);
    }

    private static StreamColumns Misaligned(string which)
    {
        double[] time = [0, 1, 2];
        double?[] ok = [1, 2, 3];
        double?[] shortColumn = [1, 2];
        double?[] longColumn = [1, 2, 3, 4];
        return which switch
        {
            "latitude-short" => new StreamColumns(time, shortColumn, ok, ok, ok, ok, ok, ok),
            "longitude-long" => new StreamColumns(time, ok, longColumn, ok, ok, ok, ok, ok),
            "altitude-short" => new StreamColumns(time, ok, ok, shortColumn, ok, ok, ok, ok),
            "heartrate-short" => new StreamColumns(time, ok, ok, ok, shortColumn, ok, ok, ok),
            "power-long" => new StreamColumns(time, ok, ok, ok, ok, longColumn, ok, ok),
            "cadence-short" => new StreamColumns(time, ok, ok, ok, ok, ok, shortColumn, ok),
            "speed-long" => new StreamColumns(time, ok, ok, ok, ok, ok, ok, longColumn),
            "time-longer-than-columns" => new StreamColumns([0, 1, 2, 3], ok, ok, ok, ok, ok, ok, ok),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, null),
        };
    }
}
