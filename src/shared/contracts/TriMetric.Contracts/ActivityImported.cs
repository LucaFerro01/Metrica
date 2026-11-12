namespace TriMetric.Contracts;

/// <summary>
/// Evento "ActivityImported" (versione 1, <c>EventEnvelope.Version = 1</c>), pubblicato da ingestion.
/// Unita' SI: metri, secondi, watt, metri al secondo; istanti UTC.
/// </summary>
/// <param name="Source">Origine dell'importazione (ad esempio "file").</param>
/// <param name="ExternalId">Identificatore dell'attivita' nella sorgente, usato per la deduplicazione.</param>
/// <param name="Sport">Sport dell'attivita'.</param>
/// <param name="StartTime">Istante UTC di inizio.</param>
/// <param name="ElapsedSeconds">Durata totale in secondi.</param>
/// <param name="MovingSeconds">Durata in movimento in secondi.</param>
/// <param name="DistanceMeters">Distanza in metri.</param>
/// <param name="ElevationGainMeters">Dislivello positivo in metri, se noto.</param>
/// <param name="Summary">Valori di sintesi.</param>
/// <param name="Laps">Giri dell'attivita'.</param>
/// <param name="Stream">Punti dello stream in formato a colonne (al massimo <see cref="StreamLimits.MaxPoints"/>).</param>
public sealed record ActivityImported(
    string Source,
    string ExternalId,
    Sport Sport,
    DateTimeOffset StartTime,
    double ElapsedSeconds,
    double MovingSeconds,
    double DistanceMeters,
    double? ElevationGainMeters,
    ActivitySummary Summary,
    IReadOnlyList<LapData> Laps,
    StreamColumns Stream);

/// <summary>
/// Valori di sintesi di un'attivita'; <c>null</c> se non disponibili.
/// </summary>
/// <param name="AvgHeartRate">Frequenza cardiaca media (battiti al minuto).</param>
/// <param name="MaxHeartRate">Frequenza cardiaca massima (battiti al minuto).</param>
/// <param name="AvgPowerWatts">Potenza media in watt.</param>
/// <param name="AvgCadence">Cadenza media (giri o passi al minuto).</param>
public sealed record ActivitySummary(
    double? AvgHeartRate,
    double? MaxHeartRate,
    double? AvgPowerWatts,
    double? AvgCadence);

/// <summary>
/// Un giro (lap) dell'attivita'.
/// </summary>
/// <param name="StartTime">Istante UTC di inizio del giro.</param>
/// <param name="ElapsedSeconds">Durata del giro in secondi.</param>
/// <param name="DistanceMeters">Distanza del giro in metri.</param>
public sealed record LapData(
    DateTimeOffset StartTime,
    double ElapsedSeconds,
    double DistanceMeters);
