using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Metrica.Contracts.Tests;

public sealed class ContractJsonTests(ITestOutputHelper output)
{
    private static readonly Guid EventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CorrelationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 6, 30, 0, TimeSpan.Zero);

    private static StreamColumns SmallStream() => new(
        TimeOffsetSeconds: [0.0, 1.0, 2.0],
        Latitude: [45.0, 45.0001, 45.0002],
        Longitude: [9.0, 9.0001, 9.0002],
        AltitudeMeters: [120.0, 120.5, 121.0],
        HeartRate: [140.0, 141.0, 142.0],
        PowerWatts: null,
        Cadence: [85.0, 86.0, 87.0],
        SpeedMetersPerSecond: [3.0, 3.1, 3.2]);

    private static EventEnvelope<ActivityImported> BuildEnvelope(Sport sport, StreamColumns stream) => new(
        Id: EventId,
        Type: EventNames.ActivityImported,
        Version: 1,
        OccurredAt: Start,
        UserId: UserId,
        CorrelationId: CorrelationId,
        Data: new ActivityImported(
            Source: "file",
            ExternalId: "abc-123",
            Sport: sport,
            StartTime: Start,
            ElapsedSeconds: 3600.5,
            MovingSeconds: 3500.25,
            DistanceMeters: 10000.0,
            ElevationGainMeters: null,
            Summary: new ActivitySummary(150.0, 180.0, null, 85.0),
            Laps: [new LapData(Start, 1800.0, 5000.0), new LapData(Start.AddSeconds(1800), 1800.5, 5000.0)],
            Stream: stream));

    private static bool SameColumn(IReadOnlyList<double?>? a, IReadOnlyList<double?>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = BuildEnvelope(Sport.Ride, SmallStream());

        var json = JsonSerializer.Serialize(original, ContractJson.Options);
        var copy = JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(json, ContractJson.Options);

        Assert.NotNull(copy);
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Type, copy.Type);
        Assert.Equal(original.Version, copy.Version);
        Assert.Equal(original.OccurredAt, copy.OccurredAt);
        Assert.Equal(original.UserId, copy.UserId);
        Assert.Equal(original.CorrelationId, copy.CorrelationId);

        var a = original.Data;
        var b = copy.Data;
        Assert.Equal(a.Source, b.Source);
        Assert.Equal(a.ExternalId, b.ExternalId);
        Assert.Equal(a.Sport, b.Sport);
        Assert.Equal(a.StartTime, b.StartTime);
        Assert.Equal(a.ElapsedSeconds, b.ElapsedSeconds);
        Assert.Equal(a.MovingSeconds, b.MovingSeconds);
        Assert.Equal(a.DistanceMeters, b.DistanceMeters);
        Assert.Equal(a.ElevationGainMeters, b.ElevationGainMeters);
        Assert.Equal(a.Summary, b.Summary);
        Assert.Equal(a.Laps.Count, b.Laps.Count);
        for (var i = 0; i < a.Laps.Count; i++)
        {
            Assert.Equal(a.Laps[i], b.Laps[i]);
        }

        Assert.True(a.Stream.TimeOffsetSeconds.SequenceEqual(b.Stream.TimeOffsetSeconds));
        Assert.True(SameColumn(a.Stream.Latitude, b.Stream.Latitude));
        Assert.True(SameColumn(a.Stream.Longitude, b.Stream.Longitude));
        Assert.True(SameColumn(a.Stream.AltitudeMeters, b.Stream.AltitudeMeters));
        Assert.True(SameColumn(a.Stream.HeartRate, b.Stream.HeartRate));
        Assert.True(SameColumn(a.Stream.PowerWatts, b.Stream.PowerWatts));
        Assert.True(SameColumn(a.Stream.Cadence, b.Stream.Cadence));
        Assert.True(SameColumn(a.Stream.SpeedMetersPerSecond, b.Stream.SpeedMetersPerSecond));
    }

    [Fact]
    public void Envelope_UsesCamelCasePropertyNames()
    {
        var json = JsonSerializer.Serialize(BuildEnvelope(Sport.Run, SmallStream()), ContractJson.Options);

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["id", "type", "version", "occurredAt", "userId", "correlationId", "data"], names);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("  ", json);
    }

    [Theory]
    [InlineData(Sport.Run, "run")]
    [InlineData(Sport.Ride, "ride")]
    [InlineData(Sport.Swim, "swim")]
    [InlineData(Sport.Other, "other")]
    public void Sport_SerializesAsLowercaseString(Sport sport, string expected)
    {
        var json = JsonSerializer.Serialize(BuildEnvelope(sport, SmallStream()), ContractJson.Options);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("data").GetProperty("sport").GetString());
    }

    [Fact]
    public void AbsentColumn_IsExplicitNullInJson_AndRoundTripsAsNull()
    {
        var json = JsonSerializer.Serialize(BuildEnvelope(Sport.Run, SmallStream()), ContractJson.Options);

        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var stream = data.GetProperty("stream");
        Assert.True(stream.TryGetProperty("powerWatts", out var power));
        Assert.Equal(JsonValueKind.Null, power.ValueKind);
        Assert.Equal(JsonValueKind.Array, stream.GetProperty("timeOffsetSeconds").ValueKind);
        Assert.True(data.TryGetProperty("elevationGainMeters", out var elevation));
        Assert.Equal(JsonValueKind.Null, elevation.ValueKind);

        var copy = JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(json, ContractJson.Options);
        Assert.NotNull(copy);
        Assert.Null(copy.Data.Stream.PowerWatts);
        Assert.NotNull(copy.Data.Stream.HeartRate);
        Assert.Null(copy.Data.ElevationGainMeters);
    }

    [Fact]
    public void NullValuesInsideColumn_SurviveRoundTrip()
    {
        var stream = SmallStream() with { HeartRate = [1.0, null, 3.0] };

        var json = JsonSerializer.Serialize(BuildEnvelope(Sport.Run, stream), ContractJson.Options);
        var copy = JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(json, ContractJson.Options);

        Assert.NotNull(copy);
        Assert.NotNull(copy.Data.Stream.HeartRate);
        Assert.Equal(new double?[] { 1.0, null, 3.0 }, copy.Data.Stream.HeartRate.ToArray());
    }

    [Fact]
    public void WorstCase_100000PointsAllColumns_StaysUnderEightMegabytes()
    {
        const int n = StreamLimits.MaxPoints;
        // Valori più lunghi possibili dopo l'arrotondamento di DEC-012.
        var time = Enumerable.Repeat(99999.999, n).ToArray();
        var lat = Enumerable.Repeat<double?>(-123.1234567, n).ToArray();
        var lon = Enumerable.Repeat<double?>(-123.1234567, n).ToArray();
        var alt = Enumerable.Repeat<double?>(-1234.5, n).ToArray();
        var speed = Enumerable.Repeat<double?>(12.345, n).ToArray();
        var hr = Enumerable.Repeat<double?>(255.0, n).ToArray();
        var power = Enumerable.Repeat<double?>(2000.0, n).ToArray();
        var cadence = Enumerable.Repeat<double?>(255.0, n).ToArray();
        var stream = new StreamColumns(time, lat, lon, alt, hr, power, cadence, speed);

        var envelope = BuildEnvelope(Sport.Run, stream);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);

        output.WriteLine($"Dimensione caso peggiore: {bytes.Length} byte ({bytes.Length / 1024.0 / 1024.0:F3} MiB), limite {8 * 1024 * 1024}");
        Assert.True(bytes.Length < 8 * 1024 * 1024, $"Dimensione {bytes.Length} byte oltre il limite di 8 MB");
    }
}
