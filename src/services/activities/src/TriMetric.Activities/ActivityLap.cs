namespace TriMetric.Activities;

/// <summary>Giro di un'attivita'. Si elimina insieme all'attivita' (ON DELETE CASCADE).</summary>
public sealed class ActivityLap
{
    public Guid Id { get; set; }

    /// <summary>Attivita' a cui appartiene il giro (chiave esterna verso <see cref="Activity.Id"/>).</summary>
    public Guid ActivityId { get; set; }

    public DateTimeOffset StartTime { get; set; }

    public double ElapsedSeconds { get; set; }

    public double DistanceMeters { get; set; }
}
