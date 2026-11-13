namespace TriMetric.Ingestion;

/// <summary>
/// Registro di un file caricato da un utente. La chiave di deduplicazione e' (<see cref="UserId"/>, <see cref="Source"/>,
/// <see cref="ExternalId"/>): lo stesso file dello stesso utente non produce una seconda attivita'.
/// </summary>
public sealed class Upload
{
    public Guid Id { get; set; }

    /// <summary>Proprietario del caricamento, sempre preso dal principal autenticato (mai dalla richiesta).</summary>
    public Guid UserId { get; set; }

    /// <summary>Fonte dell'attivita' (<c>file</c> per i caricamenti manuali).</summary>
    public required string Source { get; set; }

    /// <summary>Identificativo nella fonte (per i file, lo SHA-256 esadecimale del contenuto).</summary>
    public required string ExternalId { get; set; }

    /// <summary>Nome del file caricato, solo per visualizzazione.</summary>
    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
