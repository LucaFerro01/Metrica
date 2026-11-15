using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TriMetric.Contracts;
using TriMetric.Messaging;
using TriMetric.TestInfrastructure;

namespace TriMetric.Activities.Tests;

/// <summary>
/// <see cref="ActivityImportedHandler"/> chiamato direttamente dentro una transazione del contesto (come fa il consumatore):
/// ogni ramo della validazione, nessuna scrittura prima della validazione, nessun commit del gestore, atomicita' (REQ-014,
/// REQ-015, REQ-016, REQ-034; DEC-005). Database reale; nessuna dipendenza da NATS.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Consumer")]
[Trait("Category", "Persistence")]
public sealed class ActivityImportedHandlerTests(TimescaleFixture db)
{
    private const string Marker = "zqxprivacymarker";

    private sealed class Rig(ActivitiesDbContext context, DatabaseProbe probe) : IAsyncDisposable
    {
        public ActivitiesDbContext Db { get; } = context;

        public DatabaseProbe Probe { get; } = probe;

        public ActivityImportedHandler Handler { get; } = new(context, new StreamWriter(context));

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Rig> NewRigAsync()
    {
        await MigratedDatabase.EnsureAsync(db);
        var probe = new DatabaseProbe();
        var options = new DbContextOptionsBuilder<ActivitiesDbContext>();
        ActivitiesDbContext.Configure(options, db.ConnectionStringFor("activities"));
        options.AddInterceptors(new ProbeCommandInterceptor(probe), new ProbeTransactionInterceptor(probe));
        return new Rig(new ActivitiesDbContext(options.Options), probe);
    }

    /// <summary>Evento valido con marcatori nei campi sensibili: il messaggio di un rifiuto non deve mai riportarli.</summary>
    private static EventEnvelope<ActivityImported> Base(Guid? userId = null, int points = 20, int laps = 2)
    {
        var stream = StreamTestData.Build(points, allColumns: true) with
        {
            Latitude = Enumerable.Repeat<double?>(47.3769123, points).ToArray(),
            Longitude = Enumerable.Repeat<double?>(8.5417456, points).ToArray(),
        };
        return ImportedEvents.Envelope(
            ImportedEvents.Data($"{Marker}src", $"{Marker}ext{Guid.NewGuid():N}", Sport.Ride, points, laps) with { Stream = stream },
            userId);
    }

    private static EventEnvelope<ActivityImported> WithData(Func<ActivityImported, ActivityImported> change, Guid? userId = null)
    {
        var envelope = Base(userId);
        return envelope with { Data = change(envelope.Data) };
    }

    private static EventEnvelope<ActivityImported> WithStream(Func<StreamColumns, StreamColumns> change) =>
        WithData(d => d with { Stream = change(d.Stream) });

    private static IReadOnlyList<double?> Shorter(IReadOnlyList<double?>? column) => [.. column!.Take(column!.Count - 1)];

    private static IReadOnlyList<double?> With(IReadOnlyList<double?>? column, int index, double value)
    {
        var copy = column!.ToArray();
        copy[index] = value;
        return copy;
    }

    private static IReadOnlyList<double> WithOffset(IReadOnlyList<double> offsets, int index, double value)
    {
        var copy = offsets.ToArray();
        copy[index] = value;
        return copy;
    }

    private static readonly Dictionary<string, Func<EventEnvelope<ActivityImported>?>> InvalidCases = BuildInvalidCases();

    public static IEnumerable<object[]> InvalidCaseNames => InvalidCases.Keys.Select(k => new object[] { k });

    private static Dictionary<string, Func<EventEnvelope<ActivityImported>?>> BuildInvalidCases()
    {
        var cases = new Dictionary<string, Func<EventEnvelope<ActivityImported>?>>
        {
            ["envelope-null"] = () => null,
            ["envelope-id-empty"] = () => Base() with { Id = Guid.Empty },
            ["userId-empty"] = () => Base() with { UserId = Guid.Empty },
            ["version-2"] = () => Base() with { Version = 2 },
            ["version-0"] = () => Base() with { Version = 0 },
            ["type-other"] = () => Base() with { Type = "SomethingElse" },
            ["type-null"] = () => Base() with { Type = null! },
            ["data-null"] = () => Base() with { Data = null! },
            ["stream-null"] = () => WithData(d => d with { Stream = null! }),
            ["timeOffsetSeconds-null"] = () => WithStream(s => s with { TimeOffsetSeconds = null! }),
            ["timeOffsetSeconds-shorter"] = () => WithStream(s => s with { TimeOffsetSeconds = [.. s.TimeOffsetSeconds.Take(s.TimeOffsetSeconds.Count - 1)] }),
            ["timeOffsetSeconds-longer"] = () => WithStream(s => s with { TimeOffsetSeconds = [.. s.TimeOffsetSeconds, 99999.0] }),
            ["points-100001"] = () => WithStream(s => new StreamColumns(
                Enumerable.Range(0, StreamLimits.MaxPoints + 1).Select(i => (double)i).ToArray(), null, null, null, null, null, null, null)),
            ["sport-undefined"] = () => WithData(d => d with { Sport = (Sport)9 }),
            ["sport-negative"] = () => WithData(d => d with { Sport = (Sport)(-1) }),
            ["source-null"] = () => WithData(d => d with { Source = null! }),
            ["source-empty"] = () => WithData(d => d with { Source = string.Empty }),
            ["source-blank"] = () => WithData(d => d with { Source = " \t " }),
            ["source-too-long"] = () => WithData(d => d with { Source = new string('s', 257) }),
            ["externalId-null"] = () => WithData(d => d with { ExternalId = null! }),
            ["externalId-empty"] = () => WithData(d => d with { ExternalId = string.Empty }),
            ["externalId-blank"] = () => WithData(d => d with { ExternalId = "   " }),
            ["externalId-too-long"] = () => WithData(d => d with { ExternalId = new string('e', 257) }),
            ["externalId-nul"] = () => WithData(d => d with { ExternalId = "abc\0def" }),
            ["externalId-lone-surrogate"] = () => WithData(d => d with { ExternalId = "abc\uD800def" }),
            ["source-nul"] = () => WithData(d => d with { Source = "fi\0le" }),
            ["startTime-default"] = () => WithData(d => d with { StartTime = default }),
            ["startTime-max"] = () => WithData(d => d with { StartTime = DateTimeOffset.MaxValue }),
            ["elapsed-NaN"] = () => WithData(d => d with { ElapsedSeconds = double.NaN }),
            ["moving-infinity"] = () => WithData(d => d with { MovingSeconds = double.PositiveInfinity }),
            ["distance-negative-infinity"] = () => WithData(d => d with { DistanceMeters = double.NegativeInfinity }),
            ["elevation-NaN"] = () => WithData(d => d with { ElevationGainMeters = double.NaN }),
            ["summary-null"] = () => WithData(d => d with { Summary = null! }),
            ["summary-avgHeartRate-NaN"] = () => WithData(d => d with { Summary = d.Summary with { AvgHeartRate = double.NaN } }),
            ["summary-maxHeartRate-infinity"] = () => WithData(d => d with { Summary = d.Summary with { MaxHeartRate = double.PositiveInfinity } }),
            ["summary-avgPower-NaN"] = () => WithData(d => d with { Summary = d.Summary with { AvgPowerWatts = double.NaN } }),
            ["summary-avgCadence-infinity"] = () => WithData(d => d with { Summary = d.Summary with { AvgCadence = double.NegativeInfinity } }),
            ["laps-null"] = () => WithData(d => d with { Laps = null! }),
            ["laps-null-element"] = () => WithData(d => d with { Laps = [d.Laps[0], null!] }),
            ["lap-startTime-default"] = () => WithData(d => d with { Laps = [d.Laps[0] with { StartTime = default }] }),
            ["lap-elapsed-NaN"] = () => WithData(d => d with { Laps = [d.Laps[0], d.Laps[1] with { ElapsedSeconds = double.NaN }] }),
            ["lap-distance-infinity"] = () => WithData(d => d with { Laps = [d.Laps[0] with { DistanceMeters = double.PositiveInfinity }] }),
            ["offset-NaN"] = () => WithStream(s => s with { TimeOffsetSeconds = WithOffset(s.TimeOffsetSeconds, 4, double.NaN) }),
            ["offset-infinity"] = () => WithStream(s => s with { TimeOffsetSeconds = WithOffset(s.TimeOffsetSeconds, 4, double.PositiveInfinity) }),
            ["offset-too-large"] = () => WithStream(s => s with { TimeOffsetSeconds = WithOffset(s.TimeOffsetSeconds, 4, 1e12) }),
            ["offset-too-negative"] = () => WithStream(s => s with { TimeOffsetSeconds = WithOffset(s.TimeOffsetSeconds, 4, -1e12) }),
            ["latitude-shorter"] = () => WithStream(s => s with { Latitude = Shorter(s.Latitude) }),
            ["longitude-shorter"] = () => WithStream(s => s with { Longitude = Shorter(s.Longitude) }),
            ["altitudeMeters-shorter"] = () => WithStream(s => s with { AltitudeMeters = Shorter(s.AltitudeMeters) }),
            ["heartRate-shorter"] = () => WithStream(s => s with { HeartRate = Shorter(s.HeartRate) }),
            ["powerWatts-shorter"] = () => WithStream(s => s with { PowerWatts = Shorter(s.PowerWatts) }),
            ["cadence-shorter"] = () => WithStream(s => s with { Cadence = Shorter(s.Cadence) }),
            ["speedMetersPerSecond-shorter"] = () => WithStream(s => s with { SpeedMetersPerSecond = Shorter(s.SpeedMetersPerSecond) }),
            ["latitude-longer"] = () => WithStream(s => s with { Latitude = [.. s.Latitude!, 1.0] }),

            // Valori non finiti nelle colonne (JSON non li esprime; il lettore delle API non saprebbe serializzarli).
            ["latitude-NaN"] = () => WithStream(s => s with { Latitude = With(s.Latitude, 7, double.NaN) }),
            ["longitude-infinity"] = () => WithStream(s => s with { Longitude = With(s.Longitude, 7, double.PositiveInfinity) }),
            ["altitudeMeters-NaN"] = () => WithStream(s => s with { AltitudeMeters = With(s.AltitudeMeters, 7, double.NaN) }),
            ["heartRate-infinity"] = () => WithStream(s => s with { HeartRate = With(s.HeartRate, 7, double.NegativeInfinity) }),
            ["powerWatts-NaN"] = () => WithStream(s => s with { PowerWatts = With(s.PowerWatts, 7, double.NaN) }),
            ["cadence-infinity"] = () => WithStream(s => s with { Cadence = With(s.Cadence, 7, double.PositiveInfinity) }),
            ["speedMetersPerSecond-NaN"] = () => WithStream(s => s with { SpeedMetersPerSecond = With(s.SpeedMetersPerSecond, 7, double.NaN) }),
        };
        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidCaseNames))]
    public async Task InvalidEvent_ThrowsEventValidationException_BeforeAnyDatabaseAccess_AndTheMessageLeaksNoData(string caseName)
    {
        await using var rig = await NewRigAsync();
        var envelope = InvalidCases[caseName]();
        await using var transaction = await rig.Db.Database.BeginTransactionAsync();
        var counts = await ActivitiesData.CountsAsync(db);
        var commands = rig.Probe.Commands.Count;

        var error = await Assert.ThrowsAsync<EventValidationException>(() => rig.Handler.HandleAsync(envelope!, CancellationToken.None));

        Assert.Empty(rig.Db.ChangeTracker.Entries());   // nemmeno nel change tracker
        Assert.Equal(commands, rig.Probe.Commands.Count);   // nessun comando SQL: la validazione precede il database
        Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
        Assert.Same(transaction.GetDbTransaction(), rig.Db.Database.CurrentTransaction!.GetDbTransaction());   // transazione intatta
        // Il messaggio riporta nomi di campo e conteggi, mai valori (REQ-034).
        Assert.DoesNotContain(Marker, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("47.3769123", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("8.5417456", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task ValidEvent_AtTheBoundaries_IsAccepted()
    {
        await using var rig = await NewRigAsync();
        await using var transaction = await rig.Db.Database.BeginTransactionAsync();
        var userId = Guid.NewGuid();

        // 256 caratteri esatti nelle chiavi, nessun punto e nessun giro, tutti gli sport.
        var envelope = ImportedEvents.Envelope(
            ImportedEvents.Data(new string('s', 256), new string('e', 256), Sport.Other, 0, 0),
            userId);
        await rig.Handler.HandleAsync(envelope, CancellationToken.None);

        var inTransaction = await ScalarInTransactionAsync(rig, "SELECT count(*) FROM activities.activities WHERE user_id = @u", userId);
        Assert.Equal(1, inTransaction);
    }

    [Fact]
    public async Task Handler_DoesNotCommit_ByItself_AndLeavesNothingOnRollback()
    {
        await using var rig = await NewRigAsync();
        var envelope = Base(points: 30, laps: 2);
        var counts = await ActivitiesData.CountsAsync(db);
        await using (var transaction = await rig.Db.Database.BeginTransactionAsync())
        {
            await rig.Handler.HandleAsync(envelope, CancellationToken.None);

            // Dentro la transazione le righe ci sono, fuori (un'altra connessione) no: nessun commit del gestore.
            Assert.Equal(1, await ScalarInTransactionAsync(rig, "SELECT count(*) FROM activities.activities WHERE user_id = @u", envelope.UserId));
            Assert.Empty(await ActivitiesData.ActivitiesAsync(db, envelope.UserId));
            Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
            Assert.Same(transaction.GetDbTransaction(), rig.Db.Database.CurrentTransaction!.GetDbTransaction());   // nessuna transazione propria
            await transaction.RollbackAsync();
        }

        Assert.Empty(await ActivitiesData.ActivitiesAsync(db, envelope.UserId));
        Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
    }

    [Fact]
    public async Task Handler_AfterCommit_PersistsActivityLapsAndPoints_WithTheEnvelopeUserId()
    {
        await using var rig = await NewRigAsync();
        var envelope = Base(points: 30, laps: 2);
        await using (var transaction = await rig.Db.Database.BeginTransactionAsync())
        {
            await rig.Handler.HandleAsync(envelope, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var activity = Assert.Single(await ActivitiesData.ActivitiesAsync(db, envelope.UserId));
        Assert.Equal(envelope.UserId, activity.UserId);
        Assert.Equal(2, (await ActivitiesData.LapsAsync(db, activity.Id)).Count);
        Assert.Equal(30, await StreamTestData.CountAsync(db, activity.Id));
    }

    [Fact]
    public async Task ExistingActivity_InTheSameTransaction_AddsNoLapsAndNoPoints()
    {
        await using var rig = await NewRigAsync();
        var userId = Guid.NewGuid();
        var externalId = $"{Marker}-{Guid.NewGuid():N}";
        var first = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Run, 20, 2), userId);
        var second = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Swim, 35, 5), userId);
        await using var transaction = await rig.Db.Database.BeginTransactionAsync();

        await rig.Handler.HandleAsync(first, CancellationToken.None);
        await rig.Handler.HandleAsync(second, CancellationToken.None);   // stessa chiave: DO NOTHING, nessun errore

        Assert.Equal(1, await ScalarInTransactionAsync(rig, "SELECT count(*) FROM activities.activities WHERE user_id = @u", userId));
        Assert.Equal(2, await ScalarInTransactionAsync(rig, "SELECT count(*) FROM activities.activity_laps l JOIN activities.activities a ON a.id = l.activity_id WHERE a.user_id = @u", userId));
        Assert.Equal(20, await ScalarInTransactionAsync(rig, "SELECT count(*) FROM activities.activity_stream s JOIN activities.activities a ON a.id = s.activity_id WHERE a.user_id = @u", userId));
        Assert.Equal("run", (string)(await ScalarObjectInTransactionAsync(rig, "SELECT sport FROM activities.activities WHERE user_id = @u", userId))!);
    }

    [Fact]
    public async Task SameKeyInTwoConcurrentTransactions_TheSecondWaitsForTheFirst_ThenAddsNothing()
    {
        // L'idempotenza e' del database, non della memoria: due istanze del servizio (due transazioni) che importano lo stesso file
        // insieme. L'INSERT ... ON CONFLICT della seconda attende l'esito della prima (indice univoco) e poi non scrive nulla.
        await using var first = await NewRigAsync();
        await using var second = await NewRigAsync();
        var userId = Guid.NewGuid();
        var externalId = $"{Marker}-{Guid.NewGuid():N}";
        var a = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Run, 20, 2), userId);
        var b = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Swim, 35, 5), userId);
        await using var transactionA = await first.Db.Database.BeginTransactionAsync();
        await first.Handler.HandleAsync(a, CancellationToken.None);   // attivita' scritta, NON ancora confermata

        await using var transactionB = await second.Db.Database.BeginTransactionAsync();
        var handlingB = Task.Run(() => second.Handler.HandleAsync(b, CancellationToken.None));

        await Eventually.AssertAsync(
            async () => await LockWaitersAsync() >= 1,
            TimeSpan.FromSeconds(15),
            "la seconda transazione deve restare in attesa del lock dell'indice univoco");
        Assert.False(handlingB.IsCompleted);

        await transactionA.CommitAsync();
        await handlingB.WaitAsync(TimeSpan.FromSeconds(15));
        await transactionB.CommitAsync();

        var activity = Assert.Single(await ActivitiesData.ActivitiesAsync(db, userId));
        Assert.Equal("run", activity.Sport);   // la prima transazione vince
        Assert.Equal(2, (await ActivitiesData.LapsAsync(db, activity.Id)).Count);
        Assert.Equal(20, await StreamTestData.CountAsync(db, activity.Id));
    }

    [Fact]
    public async Task SameKeyInTwoConcurrentTransactions_WhenTheFirstRollsBack_TheSecondSavesEverything()
    {
        // Il ramo che garantisce di non perdere dati: la prima transazione annulla, la seconda (in attesa del lock) inserisce
        // davvero attivita', giri e punti del PROPRIO evento.
        await using var first = await NewRigAsync();
        await using var second = await NewRigAsync();
        var userId = Guid.NewGuid();
        var externalId = $"{Marker}-{Guid.NewGuid():N}";
        var a = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Run, 20, 2), userId);
        var b = ImportedEvents.Envelope(ImportedEvents.Data("file", externalId, Sport.Swim, 35, 5), userId);
        await using var transactionA = await first.Db.Database.BeginTransactionAsync();
        await first.Handler.HandleAsync(a, CancellationToken.None);

        await using var transactionB = await second.Db.Database.BeginTransactionAsync();
        var handlingB = Task.Run(() => second.Handler.HandleAsync(b, CancellationToken.None));
        await Eventually.AssertAsync(
            async () => await LockWaitersAsync() >= 1,
            TimeSpan.FromSeconds(15),
            "la seconda transazione deve restare in attesa del lock dell'indice univoco");
        Assert.False(handlingB.IsCompleted);

        await transactionA.RollbackAsync();
        await handlingB.WaitAsync(TimeSpan.FromSeconds(15));
        await transactionB.CommitAsync();

        var activity = Assert.Single(await ActivitiesData.ActivitiesAsync(db, userId));
        Assert.Equal("swim", activity.Sport);   // vince la seconda: la prima non ha lasciato nulla
        Assert.Equal(5, (await ActivitiesData.LapsAsync(db, activity.Id)).Count);
        Assert.Equal(35, await StreamTestData.CountAsync(db, activity.Id));
    }

    private async Task<long> LockWaitersAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE usename = 'activities_svc' AND wait_event_type = 'Lock'", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task WithoutAnActiveTransaction_Throws_BeforeWritingAnything()
    {
        await using var rig = await NewRigAsync();
        var envelope = Base(points: 10, laps: 1);
        var counts = await ActivitiesData.CountsAsync(db);

        // Senza transazione l'INSERT dell'attivita' sarebbe confermata subito e il COPY fallirebbe, lasciando un'attivita' orfana.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Handler.HandleAsync(envelope, CancellationToken.None));

        Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
        Assert.Empty(await ActivitiesData.ActivitiesAsync(db, envelope.UserId));
        Assert.Empty(rig.Probe.Commands);
    }

    [Fact]
    public async Task FailureAfterTheActivityInsert_IsNotAValidationError_AndTheRollbackLeavesNothing()
    {
        await using var rig = await NewRigAsync();
        var envelope = Base(points: 30, laps: 2);
        var counts = await ActivitiesData.CountsAsync(db);
        rig.Probe.FailLapInserts(1);

        await using (var transaction = await rig.Db.Database.BeginTransactionAsync())
        {
            // Un guasto inatteso deve far ritentare (non e' EventValidationException).
            var error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Handler.HandleAsync(envelope, CancellationToken.None));
            Assert.IsNotType<EventValidationException>(error);
            Assert.Equal(DatabaseProbe.FaultMessage, (error.InnerException ?? error).Message);   // EF avvolge l'errore di SaveChanges
            Assert.Equal(1, rig.Probe.ActivityInsertCount);   // l'attivita' era gia' stata inserita nella transazione
        }

        Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
        Assert.Empty(await ActivitiesData.ActivitiesAsync(db, envelope.UserId));
    }

    [Fact]
    public async Task CancelledToken_PropagatesTheCancellation_AndWritesNothing()
    {
        await using var rig = await NewRigAsync();
        var envelope = Base(points: 10, laps: 1);
        var counts = await ActivitiesData.CountsAsync(db);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using (var transaction = await rig.Db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Handler.HandleAsync(envelope, cancelled.Token));
        }

        Assert.Equal(counts, await ActivitiesData.CountsAsync(db));
    }

    private static async Task<long> ScalarInTransactionAsync(Rig rig, string sql, Guid userId) =>
        Convert.ToInt64(await ScalarObjectInTransactionAsync(rig, sql, userId));

    /// <summary>Legge con la connessione (e la transazione) del contesto: vede le righe non ancora confermate.</summary>
    private static async Task<object?> ScalarObjectInTransactionAsync(Rig rig, string sql, Guid userId)
    {
        var connection = (Npgsql.NpgsqlConnection)rig.Db.Database.GetDbConnection();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection, (Npgsql.NpgsqlTransaction)rig.Db.Database.CurrentTransaction!.GetDbTransaction());
        command.Parameters.AddWithValue("u", userId);
        return await command.ExecuteScalarAsync();
    }
}
