using Microsoft.AspNetCore.Http;
using TriMetric.Contracts;

namespace TriMetric.Ingestion;

/// <summary>
/// Errori di <c>POST /uploads</c> come <c>ProblemDetails</c> (RFC 7807). <c>type</c>, <c>title</c> e <c>status</c> sono stabili
/// per ogni caso; il <c>detail</c> e' un testo fisso (al massimo con un limite configurato) e non contiene mai il nome del
/// file, il suo contenuto o il testo di un'eccezione.
/// </summary>
internal static class UploadProblems
{
    private const string TypePrefix = "urn:trimetric:problem:";

    public const string InvalidFileField = "invalid-file-field";
    public const string MalformedRequest = "malformed-request";
    public const string EmptyFile = "empty-file";
    public const string UnsupportedFileType = "unsupported-file-type";
    public const string FileTooLarge = "file-too-large";
    public const string InvalidActivityFile = "invalid-activity-file";
    public const string TooManyPoints = "too-many-points";
    public const string ParseTimeout = "parse-timeout";
    public const string EventTooLarge = "event-too-large";
    public const string InternalError = "internal-error";

    public static IResult InvalidField() =>
        Problem(InvalidFileField, StatusCodes.Status400BadRequest, "Campo file non valido",
            "La richiesta deve essere multipart/form-data e contenere un solo campo 'file' con il file da caricare.");

    public static IResult Malformed() =>
        Problem(MalformedRequest, StatusCodes.Status400BadRequest, "Richiesta non valida",
            "Il corpo della richiesta non è un multipart/form-data valido.");

    public static IResult Empty() =>
        Problem(EmptyFile, StatusCodes.Status400BadRequest, "File vuoto", "Il file caricato è vuoto.");

    public static IResult UnsupportedType() =>
        Problem(UnsupportedFileType, StatusCodes.Status415UnsupportedMediaType, "Tipo di file non supportato",
            "Sono ammessi solo file con estensione .gpx o .fit.");

    public static IResult TooLarge(long maxBytes) =>
        Problem(FileTooLarge, StatusCodes.Status413PayloadTooLarge, "File troppo grande",
            $"Il file supera la dimensione massima consentita di {maxBytes} byte.");

    public static IResult InvalidActivity() =>
        Problem(InvalidActivityFile, StatusCodes.Status422UnprocessableEntity, "File non valido",
            "Il file non contiene un'attività GPX o FIT valida.");

    public static IResult TooManyPointsInFile() =>
        Problem(TooManyPoints, StatusCodes.Status422UnprocessableEntity, "Troppi punti",
            $"Il file contiene più di {StreamLimits.MaxPoints} punti.");

    public static IResult Timeout(int seconds) =>
        Problem(ParseTimeout, StatusCodes.Status422UnprocessableEntity, "Analisi troppo lunga",
            $"L'analisi del file ha superato il tempo massimo di {seconds} secondi.");

    public static IResult EventTooBig() =>
        Problem(EventTooLarge, StatusCodes.Status422UnprocessableEntity, "Attività troppo grande",
            "L'attività è troppo grande per essere importata.");

    public static IResult Internal() =>
        Problem(InternalError, StatusCodes.Status500InternalServerError, "Errore interno",
            "Errore interno durante il caricamento.");

    private static IResult Problem(string code, int status, string title, string detail) =>
        Results.Problem(detail: detail, statusCode: status, title: title, type: TypePrefix + code);
}
