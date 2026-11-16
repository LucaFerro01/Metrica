using System.Text.Json;
using System.Text.Json.Nodes;
using TriMetric.Contracts;

namespace TriMetric.IntegrationTests.Support;

/// <summary>
/// Eventi <c>ActivityImported</c> costruiti dai test come li pubblicherebbe ingestion (per l'utente B dell'isolamento, per gli
/// eventi consegnati due volte, per quelli non elaborabili). Ogni evento ha identificativi nuovi.
/// </summary>
internal static class EventFactory
{
    public static EventEnvelope<ActivityImported> Valid(
        Guid userId,
        DateTimeOffset? start = null,
        string? externalId = null,
        Sport sport = Sport.Run,
        int points = 5,
        double distanceMeters = 1234.5,
        IReadOnlyList<LapData>? laps = null,
        Guid? eventId = null)
    {
        var begin = start ?? new DateTimeOffset(2024, 6, 1, 6, 0, 0, TimeSpan.Zero);
        var stream = new StreamColumns(
            TimeOffsetSeconds: [.. Enumerable.Range(0, points).Select(i => (double)i)],
            Latitude: [.. Enumerable.Range(0, points).Select(i => (double?)(41.1 + i * 0.0001))],
            Longitude: [.. Enumerable.Range(0, points).Select(_ => (double?)12.5)],
            AltitudeMeters: [.. Enumerable.Range(0, points).Select(i => (double?)(50 + i))],
            HeartRate: [.. Enumerable.Range(0, points).Select(i => (double?)(120 + i))],
            PowerWatts: null,
            Cadence: null,
            SpeedMetersPerSecond: null);
        var data = new ActivityImported(
            Source: "file",
            ExternalId: externalId ?? $"hash-{Guid.NewGuid():N}",
            Sport: sport,
            StartTime: begin,
            ElapsedSeconds: Math.Max(0, points - 1),
            MovingSeconds: Math.Max(0, points - 1),
            DistanceMeters: distanceMeters,
            ElevationGainMeters: 10,
            Summary: new ActivitySummary(122, 124, null, null),
            Laps: laps ?? [],
            Stream: stream);
        return new EventEnvelope<ActivityImported>(
            Id: eventId ?? Guid.NewGuid(),
            Type: EventNames.ActivityImported,
            Version: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            UserId: userId,
            CorrelationId: Guid.NewGuid(),
            Data: data);
    }

    public static byte[] Serialize(EventEnvelope<ActivityImported> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);

    /// <summary>Serializza la busta e modifica il JSON: l'unico modo di produrre payload che i record C# non rappresentano.</summary>
    public static byte[] Mutate(EventEnvelope<ActivityImported> envelope, Action<JsonObject> mutation)
    {
        var root = JsonSerializer.SerializeToNode(envelope, ContractJson.Options)!.AsObject();
        mutation(root);
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
