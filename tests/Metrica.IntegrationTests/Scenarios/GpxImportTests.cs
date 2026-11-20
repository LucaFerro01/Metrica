using System.Net;
using Metrica.IntegrationTests.Files;
using Metrica.IntegrationTests.Support;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Scenarios;

/// <summary>AC-001, REQ-011..015: dal file GPX caricato dal gateway all'elenco e al dettaglio, con i valori attesi calcolati dal file.</summary>
[Collection(E2eCollection.Name)]
public sealed class GpxImportTests(TimescaleFixture db, NatsFixture nats)
{
    // Il raggio terrestre del servizio (6.371.008,8 m) differisce da quello del test (6.371.000 m) di 1,4 parti per milione.
    private const double DistanceTolerance = 1e-4;

    [Fact]
    public async Task Ac001_RunGpx_AppearsOnTopOfTheList_WithDateDistanceDurationAndElevationFromTheFile()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);

        // Una corsa piu' vecchia (caricata per prima) e quella da verificare, piu' recente, con una pausa a meta': due segmenti.
        var older = new GpxTrack { Seed = Guid.NewGuid(), Points = 300, Start = new DateTimeOffset(2024, 4, 1, 6, 0, 0, TimeSpan.Zero) };
        var track = new GpxTrack
        {
            Seed = Guid.NewGuid(),
            Points = 600,
            Start = new DateTimeOffset(2024, 5, 1, 6, 30, 15, TimeSpan.Zero),
            Segments = 2,
            PauseSeconds = 60,
        };
        var olderFile = older.Build();
        var file = track.Build();

        var first = await system.UploadAsync(olderFile);
        Expect.Status(HttpStatusCode.Accepted, first);
        await system.Api.WaitForActivityAsync(olderFile.Sha256);

        var response = await system.UploadAsync(file);
        Expect.Status(HttpStatusCode.Accepted, response);
        Assert.Equal("accepted", response.UploadStatus);
        Assert.Equal(file.Sha256, response.ExternalId);
        Assert.NotEqual(Guid.Empty, response.UploadId);

        await system.Api.WaitForActivityAsync(file.Sha256);
        var list = await system.Api.ListPageAsync();

        Assert.Equal(2, list.Total);
        var top = list.Items[0];
        Assert.Equal(file.Sha256, top.ExternalId);   // in cima: la piu' recente
        Assert.Equal(olderFile.Sha256, list.Items[1].ExternalId);
        Assert.Equal("run", top.Sport);
        Expect.SameInstant(track.ExpectedStart, top.StartTime, "data di inizio");
        Expect.CloseRelative(track.ExpectedDistanceMeters, top.DistanceMeters, DistanceTolerance, "distanza");
        Expect.Close(track.ExpectedElapsedSeconds, top.ElapsedSeconds, 0.001, "durata");
        Expect.Close(track.ExpectedElevationGainMeters, top.ElevationGainMeters, 0.001, "dislivello");

        // Dettaglio: riepilogo, giri e stream coerenti con il file.
        var detail = await system.Api.DetailOfAsync(top.Id);
        Assert.Equal(top.Id, detail.Id);
        Expect.Close(track.ExpectedMovingSeconds, detail.MovingSeconds, 0.001, "tempo in movimento (la pausa non conta)");
        Expect.Close(track.ExpectedAverageHeartRate, detail.Summary.AvgHeartRate, 0.001, "FC media");
        Expect.Close(Enumerable.Range(0, track.Points).Max(track.HeartRate), detail.Summary.MaxHeartRate, 0.001, "FC massima");
        Expect.Close(track.ExpectedAveragePower, detail.Summary.AvgPowerWatts, 0.001, "potenza media");
        Expect.Close(track.ExpectedAverageCadence, detail.Summary.AvgCadence, 0.001, "cadenza media");

        var expectedLaps = track.ExpectedLaps;
        Assert.Equal(expectedLaps.Count, detail.Laps.Count);
        for (var k = 0; k < expectedLaps.Count; k++)
        {
            Expect.SameInstant(expectedLaps[k].Start, detail.Laps[k].StartTime, $"inizio del giro {k + 1}");
            Expect.Close(expectedLaps[k].ElapsedSeconds, detail.Laps[k].ElapsedSeconds, 0.001, $"durata del giro {k + 1}");
            Expect.CloseRelative(expectedLaps[k].DistanceMeters, detail.Laps[k].DistanceMeters, DistanceTolerance, $"distanza del giro {k + 1}");
        }

        // 600 punti < 1000: lo stream e' completo, punto per punto.
        var stream = detail.Stream;
        Assert.Equal(track.Points, stream.TimeOffsetSeconds.Count);
        Assert.NotNull(stream.Latitude);
        Assert.NotNull(stream.Longitude);
        Assert.NotNull(stream.AltitudeMeters);
        Assert.NotNull(stream.HeartRate);
        Assert.NotNull(stream.PowerWatts);
        Assert.NotNull(stream.Cadence);
        Assert.NotNull(stream.SpeedMetersPerSecond);
        foreach (var i in new[] { 0, 1, 299, 300, 301, track.Points - 1 })
        {
            Expect.Close(track.OffsetMs(i) / 1000.0, stream.TimeOffsetSeconds[i], 0.0005, $"scarto di tempo del punto {i}");
            Expect.Close(track.ExpectedLatitude(i), stream.Latitude![i], 1e-9, $"latitudine del punto {i}");
            Expect.Close(track.ExpectedLongitude(i), stream.Longitude![i], 1e-9, $"longitudine del punto {i}");
            Expect.Close(track.ExpectedAltitude(i), stream.AltitudeMeters![i], 1e-9, $"quota del punto {i}");
            Expect.Close(track.HeartRate(i), stream.HeartRate![i], 0, $"FC del punto {i}");
            Expect.Close(track.Power(i), stream.PowerWatts![i], 0, $"potenza del punto {i}");
            Expect.Close(track.Cadence(i), stream.Cadence![i], 0, $"cadenza del punto {i}");
            Expect.Close(track.ExpectedSpeed(i), stream.SpeedMetersPerSecond![i], 1e-9, $"velocita' del punto {i}");
        }

        // Stream ridotto a richiesta: sempre con primo e ultimo punto.
        var reduced = (await system.Api.DetailOfAsync(top.Id, "?maxPoints=50")).Stream;
        Assert.Equal(50, reduced.TimeOffsetSeconds.Count);
        Expect.Close(0, reduced.TimeOffsetSeconds[0], 0.0005, "primo punto ridotto");
        Expect.Close(track.ExpectedElapsedSeconds, reduced.TimeOffsetSeconds[^1], 0.0005, "ultimo punto ridotto");
        Expect.Close(track.ExpectedLatitude(track.Points - 1), reduced.Latitude![^1], 1e-9, "latitudine dell'ultimo punto ridotto");

        // Il database ha lo stesso stream completo.
        var row = Assert.Single(await system.Db.ActivitiesOfAsync(system.UserId), a => a.ExternalId == file.Sha256);
        Assert.Equal(track.Points, await system.Db.StreamPointCountAsync(row.Id));

        system.AssertLogsContainNoPersonalData();
    }
}
