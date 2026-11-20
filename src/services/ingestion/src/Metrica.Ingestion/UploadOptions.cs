namespace Metrica.Ingestion;

/// <summary>
/// Opzioni dell'upload (sezione <c>Upload</c>), validate all'avvio. I valori predefiniti sono quelli di <c>appsettings.json</c>.
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>
    /// Tetto assoluto accettato per <see cref="MaxBytes"/> (64 MiB): il file sta tutto in memoria (circa 3 volte la sua
    /// dimensione per richiesta) e un FIT oltre 32 MiB e' comunque rifiutato dal parser.
    /// </summary>
    public const long MaxBytesCeiling = 64L * 1024 * 1024;

    /// <summary>Tetto assoluto di <see cref="ParseTimeoutSeconds"/> (10 minuti).</summary>
    public const int ParseTimeoutCeilingSeconds = 600;

    /// <summary>Dimensione massima del file in byte (predefinito 32 MiB = 33.554.432: un GPX da 100.000 punti con tutte le colonne pesa circa 26-29 MiB).</summary>
    public long MaxBytes { get; set; } = 33_554_432;

    /// <summary>Tempo massimo di parsing in secondi (predefinito 30).</summary>
    public int ParseTimeoutSeconds { get; set; } = 30;

    public bool IsValid() =>
        MaxBytes is >= 1 and <= MaxBytesCeiling
        && ParseTimeoutSeconds is >= 1 and <= ParseTimeoutCeilingSeconds;
}
