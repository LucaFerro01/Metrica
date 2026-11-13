namespace TriMetric.Ingestion;

/// <summary>
/// Legge un file di attivita' (GPX, FIT) e lo traduce nel modello comune. Le librerie dei formati restano
/// dietro questa interfaccia (DEC-002). Le implementazioni non scrivono log e non mettono contenuto del file
/// nei messaggi delle eccezioni; non hanno stato e si possono condividere tra richieste.
/// </summary>
public interface IActivityFileParser
{
    /// <summary>Vero se l'estensione del nome file e' quella gestita (senza distinzione di maiuscole).</summary>
    bool CanParse(string fileName);

    /// <summary>
    /// Analizza il contenuto. Il token e' controllato durante tutta la lettura: all'annullamento il metodo lancia
    /// <see cref="OperationCanceledException"/> in tempi brevi.
    /// </summary>
    /// <exception cref="ActivityFileException">File non valido, malformato, corrotto o senza i dati necessari.</exception>
    /// <exception cref="TooManyPointsException">Piu' di <c>StreamLimits.MaxPoints</c> punti.</exception>
    /// <exception cref="OperationCanceledException">Il token e' stato annullato.</exception>
    ParsedActivity Parse(Stream content, CancellationToken ct);
}
