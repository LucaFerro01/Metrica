using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriMetric.Contracts;
using TriMetric.Ingestion.Parsing;
using TriMetric.Messaging;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// <c>POST /uploads</c> (T10): esiti, deduplicazione, utenti, concorrenza. Ogni test usa un utente proprio sul database
/// condiviso, quindi conta solo le righe di quell'utente.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadTests(TimescaleFixture db, NatsFixture nats)
{
    private UploadApp Start(IReadOnlyDictionary<string, string?>? settings = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? services = null, Guid? userId = null) =>
        UploadApp.Start(db, nats, settings, services, userId: userId);

    // ---- Esito felice

    [Fact]
    public async Task ValidGpx_Returns202_OneUploadRow_OneOutboxRow_WithAnEventCoherentWithTheFile()
    {
        await using var app = Start();
        var content = UploadSamples.Run();
        var correlation = Guid.NewGuid();
        var expected = new GpxParser().Parse(new MemoryStream(content), CancellationToken.None);

        var reply = await app.UploadAsync("corsa.gpx", content, correlationId: correlation.ToString());

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        Assert.Equal("application/json", reply.MediaType);
        Assert.Equal("accepted", reply.ResponseStatus);
        Assert.Equal(UploadSamples.Sha256(content), reply.ExternalId);

        var upload = Assert.Single(await UploadDb.UploadsAsync(db, app.UserId));
        Assert.Equal(reply.UploadId, upload.Id);
        Assert.Equal("file", upload.Source);
        Assert.Equal(UploadSamples.Sha256(content), upload.ExternalId);
        Assert.Equal("corsa.gpx", upload.FileName);
        Assert.Equal(content.Length, upload.SizeBytes);
        Assert.True(DateTimeOffset.UtcNow - upload.CreatedAt < TimeSpan.FromMinutes(5));

        var row = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
        Assert.Equal(EventNames.ActivityImportedSubject, row.Subject);
        Assert.Equal(EventNames.ActivityImported, row.Type);
        var envelope = row.Envelope;
        Assert.Equal(row.Id, envelope.Id);
        Assert.NotEqual(Guid.Empty, envelope.Id);
        Assert.Equal(EventNames.ActivityImported, envelope.Type);
        Assert.Equal(1, envelope.Version);
        Assert.Equal(app.UserId, envelope.UserId);
        Assert.Equal(correlation, envelope.CorrelationId);
        Assert.True(DateTimeOffset.UtcNow - envelope.OccurredAt < TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, envelope.OccurredAt.Offset);

        var data = envelope.Data;
        Assert.Equal("file", data.Source);
        Assert.Equal(UploadSamples.Sha256(content), data.ExternalId);
        Assert.Equal(Sport.Run, data.Sport);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 6, 0, 0, TimeSpan.Zero), data.StartTime);
        Assert.Equal(40.0, data.ElapsedSeconds);
        Assert.Equal(expected.MovingSeconds, data.MovingSeconds);
        Assert.Equal(expected.DistanceMeters, data.DistanceMeters);
        Assert.True(data.DistanceMeters is > 100 and < 200, $"distanza inattesa: {data.DistanceMeters}");
        Assert.Equal(4.0, data.ElevationGainMeters);
        Assert.Equal(150.0, data.Summary.AvgHeartRate);
        Assert.Equal(160.0, data.Summary.MaxHeartRate);
        Assert.Equal(5, data.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(expected.Stream.Latitude, data.Stream.Latitude);
        Assert.Equal(expected.Laps, data.Laps);
    }

    [Fact]
    public async Task SampleRunFile_ProducesTheValuesVerifiedByHand()
    {
        await using var app = Start();
        var content = await File.ReadAllBytesAsync(SampleFiles.PathOf("run.gpx"));

        var reply = await app.UploadAsync("run.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        var data = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId)).Envelope.Data;
        Assert.Equal(Sport.Run, data.Sport);
        Assert.Equal(61, data.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(150.0, data.Summary.AvgHeartRate);
        Assert.Equal(10.0, data.ElevationGainMeters);
    }

    [Fact]
    public async Task ValidFit_Ride_Returns202_WithHeartRateAndPowerInTheEvent()
    {
        await using var app = Start();
        var content = UploadSamples.Ride();

        var reply = await app.UploadAsync("uscita.FIT", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        var data = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId)).Envelope.Data;
        Assert.Equal(Sport.Ride, data.Sport);
        Assert.Equal(130.0, data.Summary.AvgHeartRate);
        Assert.Equal(210.0, data.Summary.AvgPowerWatts);
        Assert.Equal(61, data.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(2, data.Laps.Count);
        Assert.Equal("uscita.FIT", Assert.Single(await UploadDb.UploadsAsync(db, app.UserId)).FileName);
    }

    // ---- Deduplicazione (AC-003)

    [Fact]
    public async Task SameFileTwice_TheSecondIs200Duplicate_AndThereIsStillOneUploadAndOneOutboxRow()
    {
        await using var app = Start();
        var content = UploadSamples.Run();

        var first = await app.UploadAsync("a.gpx", content);
        var second = await app.UploadAsync("rinominato.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("duplicate", second.ResponseStatus);
        Assert.Equal(first.UploadId, second.UploadId);
        Assert.Equal(first.ExternalId, second.ExternalId);
        Assert.Single(await UploadDb.UploadsAsync(db, app.UserId));
        Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
    }

    [Fact]
    public async Task DuplicateIsDetectedBeforeParsing_SoAParserThatWouldFailIsNotInvoked()
    {
        var content = UploadSamples.Run();
        var userId = Guid.NewGuid();
        await using (var first = Start(userId: userId))
        {
            Assert.Equal(HttpStatusCode.Accepted, (await first.UploadAsync("a.gpx", content)).Status);
        }

        var counting = new CountingParser();
        await using var second = Start(services: ParserOverrides.Replace(counting), userId: userId);
        var reply = await second.UploadAsync("a.gpx", content);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal("duplicate", reply.ResponseStatus);
        Assert.Equal(0, counting.Calls);
        Assert.Single(await UploadDb.OutboxAsync(db, userId));
    }

    [Fact]
    public async Task SameFileFromTwoUsers_IsAcceptedSeparately()
    {
        var content = UploadSamples.Run();
        await using var first = Start();
        await using var second = Start();   // secondo host con un altro Dev:UserId, stesso database

        var a = await first.UploadAsync("a.gpx", content);
        var b = await second.UploadAsync("a.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, a.Status);
        Assert.Equal(HttpStatusCode.Accepted, b.Status);
        Assert.NotEqual(a.UploadId, b.UploadId);
        Assert.Equal(a.ExternalId, b.ExternalId);   // l'hash e' lo stesso: cambia la chiave per utente
        var rowA = Assert.Single(await UploadDb.OutboxAsync(db, first.UserId));
        var rowB = Assert.Single(await UploadDb.OutboxAsync(db, second.UserId));
        Assert.NotEqual(rowA.Id, rowB.Id);
        Assert.Equal(first.UserId, rowA.Envelope.UserId);
        Assert.Equal(second.UserId, rowB.Envelope.UserId);
        Assert.Single(await UploadDb.UploadsAsync(db, first.UserId));
        Assert.Single(await UploadDb.UploadsAsync(db, second.UserId));
    }

    // ---- Concorrenza (REQ-011, REQ-012)

    [Fact]
    public async Task EightConcurrentUploadsOfTheSameFile_ProduceOneUploadOneOutboxRowAndExactlyOne202()
    {
        await using var app = Start();
        var content = UploadSamples.Run();
        var gate = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            await gate.Task;
            return await app.UploadAsync($"c{i}.gpx", content);
        }).ToArray();
        gate.SetResult();
        var replies = await Task.WhenAll(tasks);

        Assert.Equal(1, replies.Count(r => r.Status == HttpStatusCode.Accepted));
        Assert.Equal(7, replies.Count(r => r.Status == HttpStatusCode.OK && r.ResponseStatus == "duplicate"));
        Assert.Single(replies.Select(r => r.UploadId).Distinct());
        Assert.Single(await UploadDb.UploadsAsync(db, app.UserId));
        Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
    }

    [Fact]
    public async Task EightUploadsThatAllPassTheDuplicateCheckBeforeAnyInsert_RaceOnTheUniqueIndex_AndOnlyOneWins()
    {
        // Barriera dentro Parse: tutte e 8 le richieste hanno gia' superato il controllo "esiste gia'?" quando una qualunque
        // arriva all'insert. Il perdente deve dare "duplicate" (23505), con rollback anche della riga di outbox.
        var barrier = new BarrierParser(new GpxParser(), 8);
        await using var app = Start(services: ParserOverrides.Replace(barrier));
        var content = UploadSamples.Run();

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => app.UploadAsync($"c{i}.gpx", content)));

        Assert.True(barrier.AllArrived, "tutte le richieste dovevano superare il controllo dei duplicati prima dell'insert");
        Assert.Equal(1, replies.Count(r => r.Status == HttpStatusCode.Accepted));
        Assert.Equal(7, replies.Count(r => r.Status == HttpStatusCode.OK && r.ResponseStatus == "duplicate"));
        var winner = replies.Single(r => r.Status == HttpStatusCode.Accepted);
        Assert.All(replies, r => Assert.Equal(winner.UploadId, r.UploadId));
        Assert.Single(await UploadDb.UploadsAsync(db, app.UserId));
        Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));   // il rollback ha tolto anche le righe di outbox dei perdenti
    }

    [Fact]
    public async Task IfTheOutboxRowCannotBeWritten_TheUploadRowIsRolledBackToo_SoTheFileCanBeRetried()
    {
        // Atomicita' (REQ-012): una riga di outbox che il database rifiuta (payload nullo) deve annullare anche l'upload.
        await using var app = Start(services: s =>
        {
            s.RemoveAll<IOutbox>();
            s.AddScoped<IOutbox, BrokenOutbox>();
        });

        var reply = await app.UploadAsync("a.gpx", UploadSamples.Run());

        Assert.Equal(HttpStatusCode.InternalServerError, reply.Status);
        Assert.EndsWith("internal-error", reply.ProblemType);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    // ---- Identita' (REQ-021, DEC-006)

    [Fact]
    public async Task UserIdInTheFormTheQueryOrAHeader_DoesNotChangeTheUser()
    {
        await using var app = Start();
        var impostor = Guid.NewGuid();
        var content = UploadSamples.Run();
        using var form = UploadRequests.Form("a.gpx", content, new Dictionary<string, string> { ["userId"] = impostor.ToString(), ["user_id"] = impostor.ToString() });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/uploads?userId={impostor}") { Content = form };
        request.Headers.Add("X-User-Id", impostor.ToString());

        using var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(app.UserId, Assert.Single(await UploadDb.UploadsAsync(db, app.UserId)).UserId);
        Assert.Equal(app.UserId, Assert.Single(await UploadDb.OutboxAsync(db, app.UserId)).Envelope.UserId);
        await UploadDb.AssertNothingWrittenAsync(db, impostor);
    }

    [Fact]
    public async Task Production_WithoutIdentity_Returns401_AndTheDatabaseIsNotTouched()
    {
        await using var database = await TemporaryDatabase.CreateAsync(db);
        await using var host = new IngestionHost("Production", database.ServiceConnectionString, nats.Url);
        using var client = host.Start();
        using var form = UploadRequests.Form("a.gpx", UploadSamples.Run());

        using var response = await client.PostAsync("/uploads", form);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await database.CountTablesAsync("ingestion"));
    }

    // ---- Correlation id

    [Fact]
    public async Task CorrelationId_IsTheOneOfTheRequest_AndWhenAbsentOrInvalid_TheEventCarriesTheOneReturnedInTheResponse()
    {
        await using var app = Start();
        var sent = Guid.NewGuid();

        var withHeader = await app.UploadAsync("a.gpx", UploadSamples.Run(), correlationId: sent.ToString());
        var withoutHeader = await app.UploadAsync("b.gpx", UploadSamples.Run());
        var invalid = await app.UploadAsync("c.gpx", UploadSamples.Run(), correlationId: "non-e-un-guid");
        var empty = await app.UploadAsync("d.gpx", UploadSamples.Run(), correlationId: Guid.Empty.ToString());

        var events = (await UploadDb.OutboxAsync(db, app.UserId)).ToDictionary(r => r.Envelope.Data.ExternalId, r => r.Envelope.CorrelationId);
        Assert.Equal(sent, events[withHeader.ExternalId]);
        Assert.Equal(sent.ToString(), withHeader.CorrelationHeader);
        foreach (var reply in new[] { withoutHeader, invalid, empty })
        {
            Assert.True(Guid.TryParse(reply.CorrelationHeader, out var returned));
            Assert.NotEqual(Guid.Empty, returned);
            Assert.Equal(returned, events[reply.ExternalId]);
        }

        Assert.Equal(3, new[] { withoutHeader, invalid, empty }.Select(r => r.CorrelationHeader).Distinct().Count());
    }

    [Fact]
    public async Task MissingCorrelationId_IsAnInternalError_NotAMadeUpValue()
    {
        // Un filtro piu' interno del middleware di ServiceDefaults toglie il valore: l'endpoint non deve inventarne uno.
        await using var app = Start(services: s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, DropCorrelationIdFilter>());

        var reply = await app.UploadAsync("a.gpx", UploadSamples.Run());

        Assert.Equal(HttpStatusCode.InternalServerError, reply.Status);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    private sealed class DropCorrelationIdFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Items.Remove(TriMetric.ServiceDefaults.CorrelationIdExtensions.ItemKey);
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    // ---- Opzioni (REQ-010, REQ-009)

    [Theory]
    [InlineData("Upload:MaxBytes", "0")]
    [InlineData("Upload:MaxBytes", "-5")]
    [InlineData("Upload:MaxBytes", "999999999999")]
    [InlineData("Upload:MaxBytes", "67108865")]
    [InlineData("Upload:ParseTimeoutSeconds", "0")]
    [InlineData("Upload:ParseTimeoutSeconds", "-1")]
    [InlineData("Upload:ParseTimeoutSeconds", "100000")]
    public async Task InvalidUploadOptions_AreRejectedAtStartup(string key, string value)
    {
        await using var host = IngestionHost.Development(db, nats, new Dictionary<string, string?> { [key] = value }, Guid.NewGuid());

        Assert.ThrowsAny<Exception>(() => host.Start());
    }

    [Fact]
    public void AppSettings_DeclareTheUploadDefaults()
    {
        var settings = AppSettings.Read("appsettings.json");

        Assert.True(settings.TryGet(out var max, "Upload", "MaxBytes"));
        Assert.Equal(33_554_432, max.GetInt64());
        Assert.Equal(33_554_432, new UploadOptions().MaxBytes);   // anche il valore predefinito del codice
        Assert.True(settings.TryGet(out var timeout, "Upload", "ParseTimeoutSeconds"));
        Assert.Equal(30, timeout.GetInt32());
    }

    // ---- Ordine degli eventi

    [Fact]
    public async Task SequentialUploadsOfDifferentFiles_HaveDistinctIds_AndOutboxOrderFollowsCreatedAt()
    {
        await using var app = Start();
        var replies = new List<Reply>();
        for (var i = 0; i < 5; i++)
        {
            replies.Add(await app.UploadAsync($"g{i}.gpx", UploadSamples.Run()));
        }

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Accepted, r.Status));
        var rows = await UploadDb.OutboxAsync(db, app.UserId);
        Assert.Equal(5, rows.Count);
        Assert.Equal(5, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(5, rows.Select(r => r.Envelope.Data.ExternalId).Distinct().Count());
        Assert.Equal(replies.Select(r => r.ExternalId), rows.Select(r => r.Envelope.Data.ExternalId));   // ordinati per created_at
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i].CreatedAt >= rows[i - 1].CreatedAt);
        }
    }

    // ---- Doppi di test

    /// <summary>Accoda una riga di outbox che il database non puo' scrivere (payload nullo, colonna NOT NULL).</summary>
    private sealed class BrokenOutbox(IngestionDbContext context) : IOutbox
    {
        public void Add<T>(string subject, EventEnvelope<T> envelope) =>
            context.Outbox.Add(new OutboxMessage { Id = envelope.Id, Subject = subject, Type = envelope.Type, Payload = null!, CreatedAt = DateTimeOffset.UtcNow });
    }

    private sealed class CountingParser : IActivityFileParser
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool CanParse(string fileName) => fileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase);

        public ParsedActivity Parse(Stream content, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("non doveva essere invocato");
        }
    }

    /// <summary>Decora un parser: ogni chiamata attende che tutte le altre siano arrivate (con un tetto di tempo).</summary>
    private sealed class BarrierParser(IActivityFileParser inner, int participants) : IActivityFileParser
    {
        private readonly CountdownEvent _arrived = new(participants);

        public bool AllArrived => _arrived.IsSet;

        public bool CanParse(string fileName) => inner.CanParse(fileName);

        public ParsedActivity Parse(Stream content, CancellationToken ct)
        {
            _arrived.Signal();
            _arrived.Wait(TimeSpan.FromSeconds(20), ct);
            return inner.Parse(content, ct);
        }
    }
}
