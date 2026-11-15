namespace TriMetric.Gateway;

/// <summary>
/// Opzioni del gateway (sezione <c>Gateway</c>), validate all'avvio: una chiave sconosciuta (un refuso), un valore non numerico
/// o fuori intervallo fanno fallire l'avvio con un messaggio che nomina la chiave, mai ignorati in silenzio.
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    private const long MiB = 1024 * 1024;

    /// <summary>
    /// Valore predefinito di <see cref="MaxRequestBodyBytes"/>: <c>Upload:MaxBytes</c> di ingestion (32 MiB) piu' 1 MiB di
    /// overhead multipart (intestazioni delle parti e boundary), cosi' un file al limite del servizio passa dal gateway.
    /// </summary>
    public const long DefaultMaxRequestBodyBytes = 32 * MiB + 1 * MiB;

    /// <summary>Massimo accettato per <see cref="MaxRequestBodyBytes"/>: il tetto di <c>Upload:MaxBytes</c> di ingestion (64 MiB) piu' 1 MiB.</summary>
    public const long MaxRequestBodyBytesCeiling = 64 * MiB + 1 * MiB;

    /// <summary>
    /// Dimensione massima del corpo di una richiesta in byte (chiave <c>Gateway:MaxRequestBodyBytes</c>). Vale per tutte le
    /// rotte; oltre il limite il gateway risponde 413 senza che il backend riceva la richiesta intera.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

    public bool IsValid() => MaxRequestBodyBytes is >= 1 and <= MaxRequestBodyBytesCeiling;

    /// <summary>Messaggio di errore della validazione all'avvio.</summary>
    public static string ValidationMessage =>
        $"'{SectionName}:{nameof(MaxRequestBodyBytes)}' deve essere un intero tra 1 e {MaxRequestBodyBytesCeiling}.";
}
