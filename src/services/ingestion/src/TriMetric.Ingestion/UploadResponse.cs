namespace TriMetric.Ingestion;

/// <summary>Esito di <c>POST /uploads</c>: tipo nominato e stabile del documento OpenAPI (DEC-007).</summary>
/// <param name="UploadId">Identificativo del caricamento (per un duplicato, quello del caricamento originale).</param>
/// <param name="ExternalId">SHA-256 esadecimale minuscolo del contenuto: identificativo dell'attivita' nella fonte <c>file</c>.</param>
/// <param name="Status"><c>accepted</c> (202, evento in outbox) oppure <c>duplicate</c> (200, nessun nuovo evento).</param>
public sealed record UploadResponse(Guid UploadId, string ExternalId, string Status)
{
    public const string Accepted = "accepted";

    public const string Duplicate = "duplicate";
}
