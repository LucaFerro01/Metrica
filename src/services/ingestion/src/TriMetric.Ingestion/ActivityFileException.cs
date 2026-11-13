using TriMetric.Contracts;

namespace TriMetric.Ingestion;

/// <summary>
/// Il file non e' un'attivita' valida o supportata (malformato, corrotto, senza tempo o posizione valida,
/// senza punti). Il messaggio e' generico e sicuro da mostrare o registrare: non contiene mai contenuto del file,
/// e non c'e' eccezione interna (quelle delle librerie di parsing possono citare parti del file).
/// </summary>
public class ActivityFileException : Exception
{
    public ActivityFileException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Il file contiene piu' di <see cref="StreamLimits.MaxPoints"/> punti (REQ-035). Esattamente
/// <see cref="StreamLimits.MaxPoints"/> punti sono ammessi.
/// </summary>
public sealed class TooManyPointsException : ActivityFileException
{
    public TooManyPointsException()
        : base($"Il file contiene piu' di {StreamLimits.MaxPoints} punti.")
    {
    }
}
