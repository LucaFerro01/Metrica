using System.Globalization;
using System.Text;

namespace TriMetric.Ingestion;

/// <summary>
/// Pulizia del nome file dichiarato dal client. Il nome non e' mai usato per percorsi del file system: si usa solo per
/// scegliere il parser (dall'estensione) e per <c>uploads.file_name</c> (visualizzazione). Non e' un controllo di
/// sicurezza sul contenuto: l'estensione non prova il formato, lo prova il parser.
/// </summary>
internal static class UploadFileName
{
    public const int MaxLength = 255;

    /// <summary>
    /// Solo l'ultimo segmento (si riconoscono sia <c>/</c> sia <c>\</c>, qualunque sia il sistema operativo), senza caratteri
    /// di controllo, di formato (ad esempio l'inversione bidirezionale U+202E) ne' surrogati isolati. Puo' tornare vuoto.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var start = raw.AsSpan().LastIndexOfAny('/', '\\') + 1;
        var segment = raw.AsSpan(start);
        var clean = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (char.IsHighSurrogate(c) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]))
            {
                clean.Append(c).Append(segment[++i]);
            }
            else if (char.IsSurrogate(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                // scartato
            }
            else
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    /// <summary>Al massimo <see cref="MaxLength"/> caratteri; se serve accorciare, si conserva l'estensione.</summary>
    public static string Truncate(string sanitized)
    {
        if (sanitized.Length <= MaxLength)
        {
            return sanitized;
        }

        var extension = Path.GetExtension(sanitized);
        if (extension.Length > 16)
        {
            extension = string.Empty;
        }

        var baseLength = MaxLength - extension.Length;
        if (baseLength > 0 && char.IsHighSurrogate(sanitized[baseLength - 1]))
        {
            baseLength--;   // non tagliare una coppia di surrogati
        }

        return string.Concat(sanitized.AsSpan(0, baseLength), extension);
    }
}
