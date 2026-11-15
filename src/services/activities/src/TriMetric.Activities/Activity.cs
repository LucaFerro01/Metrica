using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Attivita' salvata. La chiave di deduplicazione e' (<see cref="UserId"/>, <see cref="Source"/>, <see cref="ExternalId"/>):
/// lo stesso file dello stesso utente non produce una seconda attivita' (REQ-011, indice univoco nel database).
/// Valori in unita' SI e istanti in UTC.
/// </summary>
public sealed class Activity
{
    public Guid Id { get; set; }

    /// <summary>Proprietario dell'attivita', preso dall'evento (mai da una richiesta HTTP).</summary>
    public Guid UserId { get; set; }

    /// <summary>Fonte dell'attivita' (<c>file</c> per i caricamenti manuali).</summary>
    public required string Source { get; set; }

    /// <summary>Identificativo nella fonte (per i file, lo SHA-256 esadecimale del contenuto).</summary>
    public required string ExternalId { get; set; }

    /// <summary>Salvato come testo minuscolo (<c>run|ride|swim|other</c>, DEC-005).</summary>
    public Sport Sport { get; set; }

    public DateTimeOffset StartTime { get; set; }

    public double ElapsedSeconds { get; set; }

    public double MovingSeconds { get; set; }

    public double DistanceMeters { get; set; }

    public double? ElevationGainMeters { get; set; }

    public double? AvgHeartRate { get; set; }

    public double? MaxHeartRate { get; set; }

    public double? AvgPowerWatts { get; set; }

    public double? AvgCadence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
