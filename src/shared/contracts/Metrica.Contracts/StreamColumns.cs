namespace Metrica.Contracts;

/// <summary>
/// Stream dell'attivita' in formato a colonne: un array per campo, tutti lunghi quanto
/// <see cref="TimeOffsetSeconds"/>. Una colonna del tutto assente e' <c>null</c>; un valore
/// mancante in un punto e' <c>null</c> dentro la colonna.
/// </summary>
/// <remarks>
/// Obbligo del PRODUTTORE (ingestion), non applicato dal contratto (DEC-012): arrotondare i valori
/// prima di costruire l'evento, cosi' che il caso peggiore (100.000 punti, 8 colonne) resti sotto 8 MB.
/// Tempo a 0,001 s; latitudine e longitudine a 7 decimali; quota a 0,1 m; velocita' a 0,001 m/s;
/// frequenza cardiaca, potenza e cadenza a numeri interi.
/// </remarks>
/// <param name="TimeOffsetSeconds">Secondi dall'inizio dell'attivita': valori non negativi e <b>non decrescenti</b> (due punti con lo stesso istante restano entrambi).</param>
/// <param name="Latitude">Latitudine in gradi decimali.</param>
/// <param name="Longitude">Longitudine in gradi decimali.</param>
/// <param name="AltitudeMeters">Quota in metri.</param>
/// <param name="HeartRate">Frequenza cardiaca in battiti al minuto.</param>
/// <param name="PowerWatts">Potenza in watt.</param>
/// <param name="Cadence">Cadenza (giri o passi al minuto).</param>
/// <param name="SpeedMetersPerSecond">Velocita' in metri al secondo.</param>
public sealed record StreamColumns(
    IReadOnlyList<double> TimeOffsetSeconds,
    IReadOnlyList<double?>? Latitude,
    IReadOnlyList<double?>? Longitude,
    IReadOnlyList<double?>? AltitudeMeters,
    IReadOnlyList<double?>? HeartRate,
    IReadOnlyList<double?>? PowerWatts,
    IReadOnlyList<double?>? Cadence,
    IReadOnlyList<double?>? SpeedMetersPerSecond);
