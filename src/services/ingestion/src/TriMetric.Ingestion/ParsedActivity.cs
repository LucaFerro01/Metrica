using TriMetric.Contracts;

namespace TriMetric.Ingestion;

/// <summary>
/// Attivita' tradotta nel modello comune, indipendente dal formato del file (REQ-007). Unita' SI (metri, secondi,
/// watt, metri al secondo), istanti UTC. Le colonne dello stream sono gia' arrotondate secondo DEC-012: tempo a
/// 0,001 s, latitudine e longitudine a 7 decimali, quota a 0,1 m, velocita' a 0,001 m/s, frequenza cardiaca,
/// potenza e cadenza a numeri interi. Anche durata, distanza (0,001) e dislivello (0,1 m) sono arrotondati.
/// </summary>
/// <param name="Sport">Sport dell'attivita'.</param>
/// <param name="StartTime">Istante UTC di inizio; il tempo dei punti dello stream e' misurato da qui.</param>
/// <param name="ElapsedSeconds">Durata totale in secondi.</param>
/// <param name="MovingSeconds">Durata in movimento in secondi.</param>
/// <param name="DistanceMeters">Distanza in metri.</param>
/// <param name="ElevationGainMeters">Dislivello positivo in metri; <c>null</c> se il file non ha la quota.</param>
/// <param name="Summary">Valori di sintesi.</param>
/// <param name="Laps">Giri dell'attivita' (possono essere nessuno).</param>
/// <param name="Stream">Punti in formato a colonne, tutte lunghe quanto <c>TimeOffsetSeconds</c>.</param>
public sealed record ParsedActivity(
    Sport Sport,
    DateTimeOffset StartTime,
    double ElapsedSeconds,
    double MovingSeconds,
    double DistanceMeters,
    double? ElevationGainMeters,
    ActivitySummary Summary,
    IReadOnlyList<LapData> Laps,
    StreamColumns Stream);
