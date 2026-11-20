namespace Metrica.Contracts;

/// <summary>
/// Sport di un'attivita'. In JSON, API e database vale una stringa minuscola:
/// "run", "ride", "swim", "other".
/// </summary>
public enum Sport
{
    Run,
    Ride,
    Swim,
    Other,
}
