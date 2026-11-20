using System.Text.Json;
using Metrica.Contracts;

namespace Metrica.Ingestion.Tests;

/// <summary>Costruisce l'evento come lo pubblichera' T10 e lo serializza con le opzioni del contratto.</summary>
internal static class Serialization
{
    public static byte[] ToEnvelopeJson(ParsedActivity a)
    {
        var data = new ActivityImported(
            "file",
            new string('a', 64), // SHA-256 esadecimale
            a.Sport,
            a.StartTime,
            a.ElapsedSeconds,
            a.MovingSeconds,
            a.DistanceMeters,
            a.ElevationGainMeters,
            a.Summary,
            a.Laps,
            a.Stream);
        var envelope = new EventEnvelope<ActivityImported>(
            Guid.NewGuid(), EventNames.ActivityImported, 1, DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), data);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options);
    }
}
