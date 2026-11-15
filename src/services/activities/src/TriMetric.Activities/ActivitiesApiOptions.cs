using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Opzioni delle API di lettura (sezione <c>Activities</c>), validate all'avvio: una chiave sconosciuta (un refuso), un valore
/// non numerico o fuori intervallo fanno fallire l'avvio con un messaggio che nomina la chiave, mai ignorati in silenzio.
/// </summary>
public sealed class ActivitiesApiOptions
{
    public const string SectionName = "Activities";

    /// <summary>Punti dello stream restituiti dal dettaglio se la richiesta non indica <c>maxPoints</c> (REQ-018).</summary>
    public const int DefaultMaxPoints = 1000;

    /// <summary>
    /// Minimo di <c>maxPoints</c>: 2, cosi' che il primo e l'ultimo punto (AC-011) stiano sempre entrambi nella risposta.
    /// Con 1 la regola "primo e ultimo" sarebbe impossibile per uno stream con piu' di un punto.
    /// </summary>
    public const int MinMaxPoints = 2;

    /// <summary>Valore predefinito di <see cref="MaxStreamPoints"/>.</summary>
    public const int DefaultMaxStreamPoints = 5000;

    /// <summary>
    /// Massimo di <c>maxPoints</c> accettato da una richiesta (chiave <c>Activities:MaxStreamPoints</c>): tra
    /// <see cref="DefaultMaxPoints"/> (il predefinito deve restare valido) e <see cref="StreamLimits.MaxPoints"/>.
    /// </summary>
    public int MaxStreamPoints { get; set; } = DefaultMaxStreamPoints;

    public bool IsValid() => MaxStreamPoints is >= DefaultMaxPoints and <= StreamLimits.MaxPoints;

    /// <summary>Messaggio di errore della validazione all'avvio.</summary>
    public static string ValidationMessage =>
        $"'{SectionName}:{nameof(MaxStreamPoints)}' deve essere un intero tra {DefaultMaxPoints} e {StreamLimits.MaxPoints}.";
}
