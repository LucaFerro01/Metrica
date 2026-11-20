using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Metrica.IntegrationTests.Files;
using Metrica.IntegrationTests.Support;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Scenarios;

/// <summary>
/// REQ-034: nei log dei TRE servizi (messaggi, valori di stato, scope, eccezioni; tutti i livelli, fino a Trace) non compaiono
/// coordinate GPS, valori di frequenza cardiaca o potenza, contenuto o nome dei file, corpo degli eventi. Gli stessi controlli
/// girano alla fine degli altri scenari (file grandi, errori, DLQ, NATS fermo); qui un percorso che li tocca tutti con valori
/// distintivi (FC 173 e 171, potenza 389 e 387).
/// </summary>
[Collection(E2eCollection.Name)]
public sealed class LogPrivacyTests(TimescaleFixture db, NatsFixture nats, Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Req034_ImportDuplicateErrorsAndDeadLetter_LeaveNoPersonalDataInTheLogs()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);

        var gpx = new GpxTrack
        {
            Seed = Guid.NewGuid(),
            Points = 400,
            Start = new DateTimeOffset(2024, 8, 1, 6, 0, 0, TimeSpan.Zero),
            ConstantHeartRate = 173,
            ConstantPower = 389,
        }.Build();
        var fit = new FitRide { Seed = Guid.NewGuid(), ConstantHeartRate = 171, ConstantPower = 387 }.Build();

        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(gpx));
        Expect.Status(HttpStatusCode.Accepted, await system.UploadAsync(fit));
        Expect.Status(HttpStatusCode.OK, await system.UploadAsync(gpx));   // duplicato
        var item = await system.Api.WaitForActivityAsync(gpx.Sha256);
        await system.Api.WaitForActivityAsync(fit.Sha256);
        await system.Api.DetailOfAsync(item.Id);
        Expect.Status(HttpStatusCode.NotFound, await system.Api.DetailAsync(Guid.NewGuid()));

        // File non validi con dati veri dentro (GPX troncato, FIT falso) e con un nome distintivo.
        var broken = Encoding.UTF8.GetString(gpx.Content);
        var truncated = new GeneratedFile($"troncato-{Guid.NewGuid():N}.gpx", Encoding.UTF8.GetBytes(broken[..(broken.Length / 3)]), gpx.Probes);
        Expect.Status(HttpStatusCode.UnprocessableEntity, await system.UploadAsync(truncated));
        var fake = new GeneratedFile($"finto-{Guid.NewGuid():N}.fit", Encoding.UTF8.GetBytes(broken[..2000]), gpx.Probes);
        Expect.Status(HttpStatusCode.UnprocessableEntity, await system.UploadAsync(fake));
        var text = new GeneratedFile($"note-{Guid.NewGuid():N}.txt", gpx.Content, gpx.Probes);
        Expect.Status(HttpStatusCode.UnsupportedMediaType, await system.UploadAsync(text));

        // Eventi non elaborabili con contenuto distintivo: finiscono in DLQ, ma il loro contenuto non va nei log.
        var marker = $"segreto-{Guid.NewGuid():N}";
        system.Protect(new PrivacyProbes().AddText("contenuto dell'evento non elaborabile", marker));
        await system.NatsProbe.PublishEventAsync(Encoding.UTF8.GetBytes($"{{ non json {marker} {gpx.Probes.Count}"));
        var invalid = EventFactory.Mutate(
            EventFactory.Valid(system.UserId, externalId: $"hash-{marker}"),
            root => root["data"]!["stream"]!["latitude"]!.AsArray().RemoveAt(0));
        await system.NatsProbe.PublishEventAsync(invalid);
        await Eventually.ValueAsync(
            () => system.NatsProbe.DlqCountAsync(), count => count >= 2, Eventually.Normal, "i due messaggi non elaborabili sono in DLQ");

        // Nel database e' arrivato tutto, e i log non sono vuoti (anche di livello Debug/Trace): il controllo non passa a vuoto.
        Assert.True(system.Logs.Count > 50, $"pochi log catturati: {system.Logs.Count}");
        Assert.Contains(system.Logs.Entries, e => e.Service == "ingestion");
        Assert.Contains(system.Logs.Entries, e => e.Service == "activities");
        Assert.Contains(system.Logs.Entries, e => e.Service == "gateway");

        foreach (var group in system.Logs.Entries.GroupBy(e => (e.Service, e.Level)).OrderBy(g => g.Key.Service).ThenBy(g => g.Key.Level))
        {
            output.WriteLine($"{group.Key.Service,-10} {group.Key.Level,-11} {group.Count()} voci");
        }

        output.WriteLine($"Ricerche eseguite su ogni voce: {system.PrivacyProbeCount}.");
        system.AssertLogsContainNoPersonalData();
    }
}

/// <summary>
/// Dimostra che il controllo sui log NON passa a vuoto: lo stesso provider dei test cattura e il rilevatore segnala una perdita
/// in ogni forma (messaggio, valore di stato, scope, eccezione, altro separatore decimale, altra precisione) e non segnala il
/// rumore innocuo (tempi in ms, porte, identificativi).
/// </summary>
public sealed class LogLeakDetectorTests
{
    private static readonly GpxTrack Track = new()
    {
        Seed = Guid.NewGuid(),
        Points = 200,
        Start = new DateTimeOffset(2024, 8, 1, 6, 0, 0, TimeSpan.Zero),
        ConstantHeartRate = 173,
        ConstantPower = 389,
    };

    [Fact]
    public void EveryKindOfLeak_IsDetected()
    {
        var probes = Track.Probes("giro.gpx");
        var lat = Track.ExpectedLatitude(0);
        var lon = Track.ExpectedLongitude(0);
        var (logger, sink) = NewLogger();

        logger.LogInformation("Punto {Latitude}", lat);                                                      // messaggio
        logger.LogInformation("Letto un punto");
        using (logger.BeginScope(new Dictionary<string, object> { ["lat"] = lat.ToString("F7", System.Globalization.CultureInfo.GetCultureInfo("it-IT")) }))
        {
            logger.LogInformation("Elaborazione in corso");                                                  // valore di scope, virgola come separatore
        }

        logger.Log(LogLevel.Information, new EventId(1), new List<KeyValuePair<string, object?>> { new("hr", 173) }, null, (_, _) => "Frequenza non nel messaggio");
        logger.LogError(new InvalidOperationException($"errore con {Track.NameMarker}"), "Fallito");          // eccezione
        logger.LogWarning("coordinata troncata {Value}", lat.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        logger.LogDebug("potenza {Power} W", 389);
        logger.LogTrace("{Event}", "{\"timeOffsetSeconds\":[0,1,2]}");
        logger.LogInformation("longitudine {Longitude}", lon);

        var findings = probes.Find(sink.Entries);

        Assert.Equal(9, sink.Entries.Count);
        foreach (var index in Enumerable.Range(0, sink.Entries.Count).Except([1]))
        {
            Assert.True(
                probes.Find([sink.Entries[index]]).Count > 0,
                $"voce {index} non rilevata: {sink.Entries[index].Text}");
        }

        Assert.NotEmpty(findings);
        Assert.Empty(probes.Find([sink.Entries[1]]));   // la voce senza dati non e' un falso positivo
    }

    [Theory]
    [InlineData("Frequenza media 173.0")]
    [InlineData("Frequenza 173bpm")]
    [InlineData("Potenza 389W")]
    [InlineData("Valori [170,173,175]")]
    [InlineData("Valori [170, 173, 175]")]
    [InlineData("heartRate=173")]
    public void NumberVariants_AreDetected(string line)
    {
        var probes = new PrivacyProbes().AddNumber("frequenza cardiaca", 173).AddNumber("potenza", 389);
        var (logger, sink) = NewLogger();

        logger.LogInformation("{Line}", line);

        Assert.NotEmpty(probes.Find(sink.Entries));
    }

    [Fact]
    public void CoordinatePairs_AreDetected_ButNotDigitsOfALongerNumber()
    {
        // "41.1234,12.5": il secondo valore dopo la virgola e' una coordinata; "145.1234" non e' 45.1234.
        var longitude = new PrivacyProbes().AddCoordinate("longitudine", 12.5);
        var latitude = new PrivacyProbes().AddCoordinate("latitudine", 45.1234567);
        var (logger, sink) = NewLogger();

        logger.LogInformation("Punto 41.1234,12.5");
        logger.LogInformation("Valore 145.1234");
        logger.LogInformation("Punto 45,1234567 (it-IT)");

        Assert.NotEmpty(longitude.Find([sink.Entries[0]]));
        Assert.Empty(latitude.Find([sink.Entries[1]]));
        Assert.NotEmpty(latitude.Find([sink.Entries[2]]));
    }

    [Fact]
    public void HarmlessNoise_IsNotFlagged()
    {
        var probes = Track.Probes("giro.gpx");
        var (logger, sink) = NewLogger();

        logger.LogInformation("Request finished HTTP/1.1 POST http://127.0.0.1:50173/uploads - 202 - 173.2518ms");
        logger.LogInformation("Socket.ReceiveAsync Size: 71 Elapsed: 173,1861 (it-IT) e 173.1861 (invariante)");
        logger.LogInformation("Executed DbCommand (173ms) [Parameters=[], CommandType='Text', CommandTimeout='30']");
        logger.LogInformation("Connection id \"0HN5173A\" accepted on 127.0.0.1:38917");
        logger.LogInformation("Evento {EventId} del consumatore activities-activity-imported ({Sequence})", Guid.NewGuid(), 17389);
        logger.LogInformation("Model: a.ElapsedSeconds, a.AvgHeartRate, Activity.AvgPowerWatts");
        logger.LogInformation("Elapsed 173ms; 12,173 e 0.173 e 1173 sono altri numeri; porta 127.0.0.1:50173");
        logger.LogInformation("Punto 145.1234 e 1,145.1234");
        logger.LogInformation("ParentId=173f0ec59556b5cb ConnectionId=0HNQ389ABC SpanId=389a1b2c3d4e5f60");

        Assert.Empty(probes.Find(sink.Entries));
    }

    private static (ILogger Logger, LogSink Sink) NewLogger()
    {
        var sink = new LogSink();
        var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(sink.ProviderFor("prova")));
        return (factory.CreateLogger("prova"), sink);
    }
}
