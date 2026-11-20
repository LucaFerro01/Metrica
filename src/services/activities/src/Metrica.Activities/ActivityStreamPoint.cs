namespace Metrica.Activities;

/// <summary>
/// Punto dello stream di un'attivita' (riga di <c>activities.activity_stream</c>, una hypertable TimescaleDB sulla colonna
/// <c>time</c>). Entita' SENZA chiave: una hypertable non puo' avere una chiave primaria che escluda la colonna di tempo e
/// due punti possono avere lo stesso istante. I punti si scrivono con <see cref="StreamWriter"/> (COPY binario), non con
/// <c>SaveChanges</c>; il modello esiste per lo schema (migrazione) e per le letture (T13).
/// Le colonne di valori sono tutte nulle ammesse: una colonna assente nello stream resta NULL.
/// </summary>
public sealed class ActivityStreamPoint
{
    public Guid ActivityId { get; set; }

    /// <summary>Istante UTC del punto: inizio dell'attivita' piu' lo scarto in secondi.</summary>
    public DateTimeOffset Time { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public double? AltitudeMeters { get; set; }

    public double? HeartRate { get; set; }

    public double? PowerWatts { get; set; }

    public double? Cadence { get; set; }

    /// <summary>Velocita' in metri al secondo (colonna <c>speed_mps</c>).</summary>
    public double? SpeedMps { get; set; }
}
