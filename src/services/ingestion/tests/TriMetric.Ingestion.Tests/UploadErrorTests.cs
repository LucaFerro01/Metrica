using System.Net;
using System.Text;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>File non validi, troppo grandi o ostili (REQ-009, REQ-035, AC-004, AC-014): errore chiaro e nessuna scrittura.</summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadErrorTests(TimescaleFixture db, NatsFixture nats)
{
    private UploadApp Start(IReadOnlyDictionary<string, string?>? settings = null) => UploadApp.Start(db, nats, settings);

    private static void AssertProblem(Reply reply, HttpStatusCode status, string typeSuffix)
    {
        Assert.Equal(status, reply.Status);
        Assert.Equal("application/problem+json", reply.MediaType);
        Assert.Equal((int)status, reply.Json.GetProperty("status").GetInt32());
        Assert.EndsWith(typeSuffix, reply.ProblemType);
        Assert.False(string.IsNullOrWhiteSpace(reply.Json.GetProperty("title").GetString()));
    }

    // ---- Validazioni, una per una (AC-004)

    [Theory]
    [InlineData("note.txt")]
    [InlineData("corsa.gpx.txt")]
    [InlineData("corsa")]
    [InlineData("corsa.tcx")]
    [InlineData("corsa.gpx ")]
    public async Task UnsupportedExtension_Returns415(string fileName)
    {
        await using var app = Start();

        var reply = await app.UploadAsync(fileName, UploadSamples.Run());

        AssertProblem(reply, HttpStatusCode.UnsupportedMediaType, "unsupported-file-type");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Theory]
    [InlineData("corsa.GPX")]
    [InlineData("corsa.Gpx")]
    public async Task ExtensionIsCaseInsensitive(string fileName)
    {
        await using var app = Start();

        var reply = await app.UploadAsync(fileName, UploadSamples.Run());

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
    }

    [Theory]
    [InlineData("vuoto.gpx")]
    [InlineData("vuoto.fit")]
    public async Task EmptyFile_Returns400(string fileName)
    {
        await using var app = Start();

        var reply = await app.UploadAsync(fileName, []);

        AssertProblem(reply, HttpStatusCode.BadRequest, "empty-file");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task MalformedGpx_Returns422()
    {
        await using var app = Start();

        var reply = await app.UploadAsync("rotto.gpx", Encoding.UTF8.GetBytes("<gpx><trk><trkseg><trkpt lat=\"1\""));

        AssertProblem(reply, HttpStatusCode.UnprocessableEntity, "invalid-activity-file");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task GpxWithoutTime_Returns422()
    {
        await using var app = Start();
        var content = await File.ReadAllBytesAsync(SampleFiles.PathOf("gpx-no-time.gpx"));

        var reply = await app.UploadAsync("senza-tempo.gpx", content);

        AssertProblem(reply, HttpStatusCode.UnprocessableEntity, "invalid-activity-file");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task TruncatedFit_Returns422()
    {
        await using var app = Start();
        var fit = UploadSamples.Ride();

        var reply = await app.UploadAsync("troncato.fit", fit[..(fit.Length / 2)]);

        AssertProblem(reply, HttpStatusCode.UnprocessableEntity, "invalid-activity-file");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task GarbageWithAGpxName_Returns422_WithAGenericMessage()
    {
        await using var app = Start();
        var secret = "CONTENUTO-SEGRETO-" + Guid.NewGuid().ToString("N");

        var reply = await app.UploadAsync("x.gpx", Encoding.UTF8.GetBytes(secret));

        AssertProblem(reply, HttpStatusCode.UnprocessableEntity, "invalid-activity-file");
        Assert.DoesNotContain(secret, reply.Body);
        Assert.DoesNotContain("x.gpx", reply.Body);
    }

    [Fact]
    public async Task MoreThanOneFileField_Returns400()
    {
        await using var app = Start();
        using var form = new MultipartFormDataContent();
        UploadRequests.AddFile(form, "file", "a.gpx", UploadSamples.Run());
        UploadRequests.AddFile(form, "file", "b.gpx", UploadSamples.Run());

        var reply = await app.PostAsync(form);

        AssertProblem(reply, HttpStatusCode.BadRequest, "invalid-file-field");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task MoreThanOneFileField_WinsOverAnUnsupportedExtension()
    {
        await using var app = Start();
        using var form = new MultipartFormDataContent();
        UploadRequests.AddFile(form, "file", "a.txt", UploadSamples.Run());
        UploadRequests.AddFile(form, "file", "b.gpx", UploadSamples.Run());

        var reply = await app.PostAsync(form);

        AssertProblem(reply, HttpStatusCode.BadRequest, "invalid-file-field");
    }

    [Fact]
    public async Task MissingFileField_Returns400()
    {
        await using var app = Start();
        using var form = UploadRequests.Form("a.gpx", UploadSamples.Run(), fieldName: "altro");

        var reply = await app.PostAsync(form);

        AssertProblem(reply, HttpStatusCode.BadRequest, "invalid-file-field");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task FileFieldWithoutAFileName_Returns400()
    {
        await using var app = Start();
        using var form = new MultipartFormDataContent { { new StringContent("testo"), "file" } };

        var reply = await app.PostAsync(form);

        AssertProblem(reply, HttpStatusCode.BadRequest, "invalid-file-field");
    }

    [Fact]
    public async Task NotMultipart_Returns400()
    {
        await using var app = Start();
        using var content = new ByteArrayContent(UploadSamples.Run());
        content.Headers.ContentType = new("application/gpx+xml");

        var reply = await app.PostAsync(content);

        AssertProblem(reply, HttpStatusCode.BadRequest, "invalid-file-field");
    }

    [Fact]
    public async Task MalformedMultipartBody_Returns400_WithoutA500()
    {
        await using var app = Start();
        var raw = Encoding.UTF8.GetBytes("--limite\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.gpx\"\r\n\r\ncorpo senza chiusura");

        var reply = await app.PostAsync(UploadRequests.RawContent("limite", raw, chunked: false));

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task TooManyFormParts_Returns400()
    {
        await using var app = Start();
        using var form = UploadRequests.Form("a.gpx", UploadSamples.Run());
        for (var i = 0; i < 200; i++)
        {
            form.Add(new StringContent("x"), $"campo{i}");
        }

        var reply = await app.PostAsync(form);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    // ---- Dimensione (413)

    [Fact]
    public async Task FileOverMaxBytes_Returns413()
    {
        await using var app = Start(new Dictionary<string, string?> { ["Upload:MaxBytes"] = "2048" });
        var content = UploadSamples.Run();
        Assert.True(content.Length < 2048, "il file di prova deve stare sotto il limite");
        var big = content.Concat(new byte[4096]).ToArray();

        var reply = await app.UploadAsync("grande.gpx", big);

        AssertProblem(reply, HttpStatusCode.RequestEntityTooLarge, "file-too-large");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task FileOfExactlyMaxBytes_IsNotTooLarge()
    {
        var content = UploadSamples.Run();
        await using var app = Start(new Dictionary<string, string?> { ["Upload:MaxBytes"] = content.Length.ToString() });

        var reply = await app.UploadAsync("esatto.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
    }

    [Fact]
    public async Task OneByteOverMaxBytes_Returns413()
    {
        var content = UploadSamples.Run();
        await using var app = Start(new Dictionary<string, string?> { ["Upload:MaxBytes"] = (content.Length - 1).ToString() });

        var reply = await app.UploadAsync("uno-in-piu.gpx", content);

        AssertProblem(reply, HttpStatusCode.RequestEntityTooLarge, "file-too-large");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBody_Returns413_EvenWithoutADeclaredContentLength(bool chunked)
    {
        await using var app = Start(new Dictionary<string, string?> { ["Upload:MaxBytes"] = "2048" });
        // Molto oltre limite + margine del multipart: il corpo non viene letto fino in fondo in memoria.
        var big = new byte[3 * 1024 * 1024];
        Array.Fill(big, (byte)'a');
        var raw = UploadRequests.RawMultipart(
            "limite",
            ("Content-Disposition: form-data; name=\"file\"; filename=\"enorme.gpx\"\r\nContent-Type: application/octet-stream", big));
        using var content = UploadRequests.RawContent("limite", raw, chunked);
        Assert.Equal(chunked, content.Headers.ContentLength is null);

        var reply = await app.PostAsync(content);

        AssertProblem(reply, HttpStatusCode.RequestEntityTooLarge, "file-too-large");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task EmptyFile_IsRejectedWith400EvenWhenOtherFieldsAreBig()
    {
        await using var app = Start(new Dictionary<string, string?> { ["Upload:MaxBytes"] = "2048" });
        using var form = UploadRequests.Form("a.gpx", [], new Dictionary<string, string> { ["nota"] = new string('x', 4096) });

        var reply = await app.PostAsync(form);

        AssertProblem(reply, HttpStatusCode.BadRequest, "empty-file");
    }

    // ---- Limite di punti (REQ-035, AC-014)

    // Un GPX con 100.000 punti e tutte le colonne pesa 26-29 MiB (GPX e' verboso): oltre i 25 MiB, ma sotto il limite
    // PREDEFINITO di 32 MiB. Questi test usano il limite predefinito (AC-014) e verificano che il file superi davvero i 25 MiB.
    private const int TwentyFiveMiB = 25 * 1024 * 1024;

    [Fact]
    public async Task Gpx100000Points_Returns202_AndTheEventStaysUnder8MiB()
    {
        await using var app = Start();
        var content = GpxBuilder.Generate(StreamLimits.MaxPoints, allColumns: true).ToArray();
        Assert.True(content.Length > TwentyFiveMiB, $"il file di prova deve superare 25 MiB (e' di {content.Length} byte)");
        Assert.True(content.Length < new UploadOptions().MaxBytes);

        var reply = await app.UploadAsync("lungo.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        var row = Assert.Single(await UploadDb.OutboxAsync(db, app.UserId));
        Assert.True(row.Payload.Length < 8 * 1024 * 1024, $"payload di {row.Payload.Length} byte");
        Assert.Equal(StreamLimits.MaxPoints, row.Envelope.Data.Stream.TimeOffsetSeconds.Count);
        Assert.Equal(StreamLimits.MaxPoints, row.Envelope.Data.Stream.HeartRate!.Count);
    }

    [Fact]
    public async Task Gpx100000PointsWithoutExtensions_IsAcceptedWithTheDefaultLimits()
    {
        await using var app = Start();
        var content = GpxBuilder.Generate(StreamLimits.MaxPoints, allColumns: false).ToArray();
        Assert.True(content.Length < TwentyFiveMiB);

        var reply = await app.UploadAsync("lungo.gpx", content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
    }

    [Fact]
    public async Task Gpx100001Points_Returns422_TooManyPoints_WithoutRows()
    {
        await using var app = Start();
        var content = GpxBuilder.Generate(StreamLimits.MaxPoints + 1, allColumns: true).ToArray();
        Assert.True(content.Length > TwentyFiveMiB, $"il file di prova deve superare 25 MiB (e' di {content.Length} byte)");
        Assert.True(content.Length < new UploadOptions().MaxBytes);

        var reply = await app.UploadAsync("troppo-lungo.gpx", content);

        AssertProblem(reply, HttpStatusCode.UnprocessableEntity, "too-many-points");
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    // ---- Nome file ostile (il nome non si usa mai per il file system: si salva solo, ripulito)

    public static TheoryData<string, string> HostileNames => new()
    {
        { @"..\..\evil.gpx", "evil.gpx" },
        { "../../etc/passwd.gpx", "passwd.gpx" },
        { @"C:\Windows\System32\evil.gpx", "evil.gpx" },
        { "/abs/olute/path/evil.fit", "evil.fit" },
    };

    [Theory]
    [MemberData(nameof(HostileNames))]
    public async Task PathLikeFileNames_AreStoredWithoutSeparators(string sent, string stored)
    {
        await using var app = Start();
        var content = Path.GetExtension(sent) == ".fit" ? UploadSamples.Ride() : UploadSamples.Run();

        var reply = await app.UploadAsync(sent, content);

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        Assert.Equal(stored, Assert.Single(await UploadDb.UploadsAsync(db, app.UserId)).FileName);
    }

    [Fact]
    public async Task ControlCharactersInTheFileName_AreNeverStored()
    {
        await using var app = Start();
        var raw = UploadRequests.RawMultipart(
            "limite",
            ("Content-Disposition: form-data; name=\"file\"; filename=\"a\u0001b\u0007c\u202Ed\t.gpx\"", UploadSamples.Run()));

        var reply = await app.PostAsync(UploadRequests.RawContent("limite", raw, chunked: false));

        // Accettato (ripulito) oppure rifiutato in modo sicuro: mai salvato con i caratteri di controllo.
        Assert.True(reply.Status is HttpStatusCode.Accepted or HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, $"stato {reply.Status}");
        foreach (var upload in await UploadDb.UploadsAsync(db, app.UserId))
        {
            Assert.DoesNotContain(upload.FileName, char.IsControl);
            Assert.DoesNotContain('\u202E', upload.FileName);
            Assert.EndsWith(".gpx", upload.FileName);
        }
    }

    [Fact]
    public async Task VeryLongFileName_IsTruncatedTo255Characters_KeepingTheExtension()
    {
        await using var app = Start();
        var name = new string('n', 1000) + ".gpx";

        var reply = await app.UploadAsync(name, UploadSamples.Run());

        Assert.Equal(HttpStatusCode.Accepted, reply.Status);
        var stored = Assert.Single(await UploadDb.UploadsAsync(db, app.UserId)).FileName;
        Assert.Equal(255, stored.Length);
        Assert.EndsWith(".gpx", stored);
    }

    [Fact]
    public async Task FileNameThatIsOnlyDotsOrSeparators_IsNotAValidName()
    {
        await using var app = Start();

        var reply = await app.UploadAsync(@"..\..", UploadSamples.Run());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, reply.Status);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }
}
