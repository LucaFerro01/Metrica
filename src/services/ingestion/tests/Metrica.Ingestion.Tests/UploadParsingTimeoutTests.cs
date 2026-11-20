using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Metrica.TestInfrastructure;

namespace Metrica.Ingestion.Tests;

/// <summary>Tempo massimo di parsing (REQ-010) e client che si disconnette durante il parsing.</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadParsingTimeoutTests(TimescaleFixture db, NatsFixture nats)
{
    private static readonly IReadOnlyDictionary<string, string?> OneSecond = new Dictionary<string, string?> { ["Upload:ParseTimeoutSeconds"] = "1" };

    [Fact]
    public async Task SlowParserThatHonoursTheToken_Returns422ParseTimeout_WithinAFewSeconds_AndWritesNothing()
    {
        var parser = new SlowParser(honourToken: true);
        await using var app = UploadApp.Start(db, nats, OneSecond, ParserOverrides.Replace(parser));

        var watch = Stopwatch.StartNew();
        var reply = await app.UploadAsync("lento.gpx", UploadSamples.Run());
        watch.Stop();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.Status);
        Assert.Equal("application/problem+json", reply.MediaType);
        Assert.EndsWith("parse-timeout", reply.ProblemType);
        Assert.Equal(422, reply.Json.GetProperty("status").GetInt32());
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(4));
        Assert.True(parser.Started);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task SlowParserThatIgnoresTheToken_StillGets422WithinAFewSeconds()
    {
        // Rete di sicurezza: un parser che non controlla il token non deve tenere agganciata la richiesta.
        var parser = new SlowParser(honourToken: false);
        await using var app = UploadApp.Start(db, nats, OneSecond, ParserOverrides.Replace(parser));

        var watch = Stopwatch.StartNew();
        var reply = await app.UploadAsync("sordo.gpx", UploadSamples.Run());
        watch.Stop();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.Status);
        Assert.EndsWith("parse-timeout", reply.ProblemType);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(4));
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
        parser.Release();
    }

    [Fact]
    public async Task AfterATimeout_TheSameFileCanBeUploadedAgainAndIsAccepted()
    {
        var content = UploadSamples.Run();
        var userId = Guid.NewGuid();
        await using (var slow = UploadApp.Start(db, nats, OneSecond, ParserOverrides.Replace(new SlowParser(honourToken: true)), userId: userId))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await slow.UploadAsync("a.gpx", content)).Status);
        }

        await using var normal = UploadApp.Start(db, nats, userId: userId);
        var reply = await normal.UploadAsync("a.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
    }

    [Fact]
    public async Task ClientDisconnectDuringParsing_StopsQuietly_WithoutRowsOrErrorLogs()
    {
        var parser = new SlowParser(honourToken: true);
        var logs = new LogCapture();
        await using var app = UploadApp.Start(
            db, nats, new Dictionary<string, string?> { ["Upload:ParseTimeoutSeconds"] = "30" },
            ParserOverrides.Replace(parser),
            configureLogging: l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        using var cancel = new CancellationTokenSource();
        using var form = UploadRequests.Form("a.gpx", UploadSamples.Run());
        var request = app.Client.PostAsync("/uploads", form, cancel.Token);

        await Eventually.AssertAsync(() => Task.FromResult(parser.Started), TimeSpan.FromSeconds(10), "il parsing deve partire");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Eventually.AssertAsync(() => Task.FromResult(parser.Stopped), TimeSpan.FromSeconds(5), "il parser deve fermarsi al token");

        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning && e.Category.StartsWith("Metrica", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }

    /// <summary>Parser finto che non finisce mai da solo: rispetta il token oppure lo ignora (fino al rilascio o a 8 s).</summary>
    private sealed class SlowParser(bool honourToken) : IActivityFileParser
    {
        private readonly ManualResetEventSlim _release = new();
        private volatile bool _started;
        private volatile bool _stopped;

        public bool Started => _started;

        public bool Stopped => _stopped;

        public void Release() => _release.Set();

        public bool CanParse(string fileName) => fileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase);

        public ParsedActivity Parse(Stream content, CancellationToken ct)
        {
            _started = true;
            try
            {
                if (honourToken)
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        Thread.Sleep(10);
                    }
                }

                _release.Wait(TimeSpan.FromSeconds(8));
                throw new ActivityFileException("ignora il token");
            }
            finally
            {
                _stopped = true;
            }
        }
    }
}
