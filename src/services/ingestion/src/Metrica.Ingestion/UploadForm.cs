namespace Metrica.Ingestion;

/// <summary>
/// Forma del corpo <c>multipart/form-data</c> di <c>POST /uploads</c>, solo per il documento OpenAPI: il corpo si legge a
/// mano (<see cref="MultipartUploadReader"/>) e non si lega mai a questo tipo.
/// </summary>
public sealed class UploadForm
{
    /// <summary>File <c>.gpx</c> o <c>.fit</c>: l'unico campo <c>file</c> della richiesta.</summary>
    public required IFormFile File { get; init; }
}
