namespace TriMetric.Ingestion.Parsing;

/// <summary>Un punto letto dal file, con i valori nelle unita' del file (SI, gradi decimali) e non ancora arrotondati.</summary>
/// <param name="Time">Istante UTC.</param>
/// <param name="Latitude">Gradi; presente solo insieme alla longitudine.</param>
/// <param name="Longitude">Gradi.</param>
/// <param name="Altitude">Metri.</param>
/// <param name="HeartRate">Battiti al minuto.</param>
/// <param name="Power">Watt.</param>
/// <param name="Cadence">Giri o passi al minuto.</param>
/// <param name="Speed">Metri al secondo, se scritta nel file.</param>
/// <param name="CumulativeDistance">Distanza cumulata in metri, se scritta nel file (usata solo se manca la posizione).</param>
internal readonly record struct RawPoint(
    DateTimeOffset Time,
    double? Latitude,
    double? Longitude,
    double? Altitude,
    double? HeartRate,
    double? Power,
    double? Cadence,
    double? Speed,
    double? CumulativeDistance)
{
    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
}
