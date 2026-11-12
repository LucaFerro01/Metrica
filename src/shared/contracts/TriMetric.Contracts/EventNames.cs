namespace TriMetric.Contracts;

/// <summary>
/// Nomi di eventi, subject e stream JetStream condivisi tra i servizi.
/// </summary>
public static class EventNames
{
    /// <summary>Nome dell'evento di importazione di un'attivita' (versione 1).</summary>
    public const string ActivityImported = "ActivityImported";

    /// <summary>Subject su cui ingestion pubblica <see cref="ActivityImported"/>.</summary>
    public const string ActivityImportedSubject = "ingestion.activity.imported";

    /// <summary>Nome dello stream degli eventi.</summary>
    public const string EventsStream = "EVENTS";

    /// <summary>Nome dello stream dei messaggi falliti.</summary>
    public const string DlqStream = "DLQ";

    /// <summary>
    /// Subject dello stream <see cref="EventsStream"/>: prefissi espliciti dei servizi, non un pattern
    /// generico (si sovrapporrebbe a <see cref="DlqStreamSubjects"/>).
    /// </summary>
    public static readonly string[] EventsStreamSubjects =
        ["identity.>", "ingestion.>", "activities.>", "analytics.>", "training-plan.>"];

    /// <summary>Subject dello stream <see cref="DlqStream"/>.</summary>
    public static readonly string[] DlqStreamSubjects = ["dlq.>"];
}

/// <summary>
/// Limiti del contratto.
/// </summary>
public static class StreamLimits
{
    /// <summary>Numero massimo di punti dello stream in un evento (DEC-012).</summary>
    public const int MaxPoints = 100_000;
}
