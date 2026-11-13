namespace TriMetric.Ingestion.Parsing;

/// <summary>
/// Limiti di plausibilita' e tetti dei parser. Un valore fuori scala si tratta come ASSENTE (<c>null</c>), perche' un
/// valore finito ma enorme (1e308) renderebbe infiniti somme e medie e l'evento non serializzabile; un istante fuori
/// intervallo invalida il file.
/// </summary>
internal static class Plausibility
{
    // Tetti (input non attendibile).
    public const int MaxLaps = 1000;
    public const int MaxSessions = 100;
    public const int MaxXmlDepth = 32;

    /// <summary>Quota in metri: da -1.000 (Mar Morto e miniere) a 20.000.</summary>
    public const double MinAltitude = -1_000, MaxAltitude = 20_000;

    /// <summary>
    /// Frequenza cardiaca da 1 a 300 bpm: 0 e' il valore che molti dispositivi scrivono quando non hanno una
    /// lettura (non e' un battito) e abbasserebbe la media, quindi e' trattato come assente.
    /// </summary>
    public const double MinHeartRate = 1, MaxHeartRate = 300;

    public const double MinPower = 0, MaxPower = 5_000;
    public const double MinCadence = 0, MaxCadence = 300;

    /// <summary>Velocita' in m/s (360 km/h).</summary>
    public const double MinSpeed = 0, MaxSpeed = 100;

    /// <summary>Distanze e durate di sintesi: fino a 100.000 km e 1e8 s (oltre 3 anni); salita fino a 1e7 m.</summary>
    public const double MaxDistance = 1e8, MaxDuration = 1e8, MaxAscent = 1e7;

    public static readonly DateTimeOffset MinTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset MaxTime = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static double? In(double? value, double min, double max)
        => value is { } v && double.IsFinite(v) && v >= min && v <= max ? v : null;

    public static bool TimeInRange(DateTimeOffset t) => t >= MinTime && t < MaxTime;
}
