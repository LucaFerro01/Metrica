using System.Text.Json;
using System.Text.Json.Nodes;
using Metrica.Contracts;

namespace Metrica.Activities.Tests;

/// <summary>Eventi <c>ActivityImported</c> di prova e loro varianti non valide (T12).</summary>
internal static class ImportedEvents
{
    /// <summary>
    /// Inizio con offset +02:00 e una frazione di secondo (0,123456 s): il salvataggio deve conservare l'ISTANTE UTC
    /// (un <c>DateTimeOffset</c> con offset diverso da zero non e' scrivibile cosi' com'e' su <c>timestamptz</c>).
    /// </summary>
    public static readonly DateTimeOffset Start =
        new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_560);

    /// <summary>Evento valido con identificativi nuovi (utente, evento, <c>externalId</c>): nessun test dipende da un altro.</summary>
    public static EventEnvelope<ActivityImported> Valid(
        Guid? userId = null,
        string source = "file",
        string? externalId = null,
        Sport sport = Sport.Ride,
        int points = 40,
        int laps = 3,
        bool allColumns = false,
        Guid? eventId = null) =>
        Envelope(
            Data(source, externalId ?? $"hash-{Guid.NewGuid():N}", sport, points, laps, allColumns),
            userId,
            eventId);

    public static ActivityImported Data(
        string source,
        string externalId,
        Sport sport,
        int points,
        int laps,
        bool allColumns = false) =>
        new(
            Source: source,
            ExternalId: externalId,
            Sport: sport,
            StartTime: Start,
            ElapsedSeconds: 3725.5,
            MovingSeconds: 3600.25,
            DistanceMeters: 10234.75,
            ElevationGainMeters: 123.4,
            Summary: new ActivitySummary(148.5, 181, 230.5, 87),
            Laps: Laps(laps),
            Stream: StreamTestData.Build(points, allColumns));

    public static List<LapData> Laps(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new LapData(Start.AddMinutes(i * 10).ToOffset(TimeSpan.FromHours(-5)), 600 + i + 0.5, 2000.25 + i))];

    public static EventEnvelope<ActivityImported> Envelope(ActivityImported data, Guid? userId = null, Guid? eventId = null) =>
        new(
            Id: eventId ?? Guid.NewGuid(),
            Type: EventNames.ActivityImported,
            Version: 1,
            OccurredAt: new DateTimeOffset(2026, 11, 12, 10, 30, 0, TimeSpan.Zero),
            UserId: userId ?? Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            Data: data);

    public static byte[] Serialize(EventEnvelope<ActivityImported> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);

    /// <summary>
    /// Serializza la busta e applica la modifica al JSON (<c>data</c>, <c>stream</c>... con i nomi camelCase del contratto):
    /// l'unico modo di produrre payload che i record C# non possono rappresentare (campi mancanti, nulli, sport sconosciuto).
    /// </summary>
    public static byte[] Mutate(EventEnvelope<ActivityImported> envelope, Action<JsonObject> mutation)
    {
        var root = JsonSerializer.SerializeToNode(envelope, ContractJson.Options)!.AsObject();
        mutation(root);
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    public static JsonObject DataOf(JsonObject root) => root["data"]!.AsObject();

    public static JsonObject StreamOf(JsonObject root) => DataOf(root)["stream"]!.AsObject();

    /// <summary>Elimina l'ultimo elemento di un array del JSON (colonna piu' corta di una unita').</summary>
    public static void DropLast(JsonObject root, string column) =>
        StreamOf(root)[column]!.AsArray().RemoveAt(StreamOf(root)[column]!.AsArray().Count - 1);

    public static void AppendNull(JsonObject root, string column) => StreamOf(root)[column]!.AsArray().Add(null);

    /// <summary>Nomi JSON delle colonne di valori dello stream (tutte tranne <c>timeOffsetSeconds</c>).</summary>
    public static readonly string[] ValueColumns =
        ["latitude", "longitude", "altitudeMeters", "heartRate", "powerWatts", "cadence", "speedMetersPerSecond"];
}
