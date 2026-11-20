using System.Net;
using Metrica.IntegrationTests.Files;
using Metrica.IntegrationTests.Support;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Scenarios;

/// <summary>AC-002: uscita in bici in formato FIT con frequenza cardiaca e potenza, dal gateway al dettaglio e al database.</summary>
[Collection(E2eCollection.Name)]
public sealed class FitImportTests(TimescaleFixture db, NatsFixture nats)
{
    [Fact]
    public async Task Ac002_RideFit_IsImportedAsRide_WithHeartRateAndPowerAverages()
    {
        await using var system = await EndToEndSystem.StartAsync(db, nats);
        var ride = new FitRide { Seed = Guid.NewGuid() };
        var file = ride.Build();

        var response = await system.UploadAsync(file);

        Expect.Status(HttpStatusCode.Accepted, response);
        Assert.Equal(file.Sha256, response.ExternalId);
        var item = await system.Api.WaitForActivityAsync(file.Sha256);

        Assert.Equal("ride", item.Sport);
        Expect.SameInstant(new DateTimeOffset(ride.Start, TimeSpan.Zero), item.StartTime, "inizio");
        Expect.Close(ride.ExpectedDistanceMeters, item.DistanceMeters, 0.01, "distanza");
        Expect.Close(ride.ExpectedElapsedSeconds, item.ElapsedSeconds, 0.001, "durata");
        Expect.Close(ride.ExpectedElevationGainMeters, item.ElevationGainMeters, 0.05, "dislivello");

        var detail = await system.Api.DetailOfAsync(item.Id);
        Expect.Close(ride.ExpectedAverageHeartRate, detail.Summary.AvgHeartRate, 0.001, "FC media");
        Expect.Close(ride.ExpectedMaxHeartRate, detail.Summary.MaxHeartRate, 0.001, "FC massima");
        Expect.Close(ride.ExpectedAveragePower, detail.Summary.AvgPowerWatts, 0.001, "potenza media");
        Expect.Close(ride.ExpectedAverageCadence, detail.Summary.AvgCadence, 0.001, "cadenza media");

        Assert.Equal(ride.ExpectedLaps.Count, detail.Laps.Count);
        for (var k = 0; k < detail.Laps.Count; k++)
        {
            Expect.Close(ride.ExpectedLaps[k].ElapsedSeconds, detail.Laps[k].ElapsedSeconds, 0.001, $"durata del giro {k + 1}");
            Expect.Close(ride.ExpectedLaps[k].DistanceMeters, detail.Laps[k].DistanceMeters, 0.01, $"distanza del giro {k + 1}");
        }

        // Stream completo (61 punti): FC e potenza punto per punto.
        var stream = detail.Stream;
        Assert.Equal(ride.Records, stream.TimeOffsetSeconds.Count);
        Assert.NotNull(stream.HeartRate);
        Assert.NotNull(stream.PowerWatts);
        Assert.NotNull(stream.Latitude);
        Assert.NotNull(stream.Longitude);
        for (var i = 0; i < ride.Records; i += 6)
        {
            Expect.Close(i, stream.TimeOffsetSeconds[i], 0.0005, $"scarto di tempo del record {i}");
            Expect.Close(ride.HeartRate(i), stream.HeartRate![i], 0, $"FC del record {i}");
            Expect.Close(ride.Power(i), stream.PowerWatts![i], 0, $"potenza del record {i}");
            Expect.Close(ride.Latitude(i), stream.Latitude![i], 1e-7, $"latitudine del record {i}");
            Expect.Close(ride.Longitude, stream.Longitude![i], 1e-7, $"longitudine del record {i}");
        }

        // Anche la riga nel database (verificata come superutente, non dal servizio).
        var row = Assert.Single(await system.Db.ActivitiesOfAsync(system.UserId));
        Assert.Equal("ride", row.Sport);
        Assert.Equal(file.Sha256, row.ExternalId);
        Assert.Equal("file", row.Source);
        Expect.Close(ride.ExpectedAverageHeartRate, row.AvgHeartRate, 0.001, "DB: FC media");
        Expect.Close(ride.ExpectedMaxHeartRate, row.MaxHeartRate, 0.001, "DB: FC massima");
        Expect.Close(ride.ExpectedAveragePower, row.AvgPowerWatts, 0.001, "DB: potenza media");
        Assert.Equal(ride.Records, await system.Db.StreamPointCountAsync(row.Id));
        var points = await system.Db.StreamAsync(row.Id, new[] { 0, 30, 60 }.Select(i => ride.Start.AddSeconds(i)));
        Assert.Equal(3, points.Count);
        Assert.Equal([ride.HeartRate(0), ride.HeartRate(30), ride.HeartRate(60)], points.Select(p => (int)p.HeartRate!.Value));
        Assert.Equal([ride.Power(0), ride.Power(30), ride.Power(60)], points.Select(p => (int)p.Power!.Value));

        system.AssertLogsContainNoPersonalData();
    }
}
