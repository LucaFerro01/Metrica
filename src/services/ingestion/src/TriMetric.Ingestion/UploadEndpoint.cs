using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Npgsql;
using TriMetric.Contracts;
using TriMetric.Messaging;
using TriMetric.ServiceDefaults;

namespace TriMetric.Ingestion;

/// <summary>
/// <c>POST /uploads</c>: riceve un file GPX o FIT, lo valida, lo deduplica per contenuto e pubblica <c>ActivityImported</c>
/// tramite outbox nella stessa transazione del registro dei caricamenti (REQ-006, REQ-009..REQ-013).
/// Il file non e' attendibile: dimensione, tempo di parsing, nome e contenuto sono tutti limitati, e nessun dato del file ne' il
/// suo nome finisce nei log o nelle risposte di errore (REQ-034).
/// </summary>
internal static class UploadEndpoint
{
    public const string Route = "/uploads";
    public const string Source = "file";
    public const string OperationName = "UploadActivityFile";

    /// <summary>Margine per l'overhead multipart (intestazioni, boundary, altri campi) oltre <c>Upload:MaxBytes</c>.</summary>
    private const long MultipartOverheadBytes = 1024 * 1024;

    /// <summary>L'evento deve restare sotto il <c>max_payload</c> di NATS (8 MiB, DEC-012).</summary>
    private const int EventSizeLimitBytes = 8 * 1024 * 1024;

    private const string LoggerCategory = "TriMetric.Ingestion.Upload";

    public static IEndpointRouteBuilder MapUploadEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, HandleAsync)
            .RequireAuthorization()
            .DisableAntiforgery()   // il corpo non si lega a IFormFile (lettura a mano): resta esplicito che non c'e' antiforgery
            .WithName(OperationName)
            .WithSummary("Carica un file di attività GPX o FIT.")
            .Produces<UploadResponse>(StatusCodes.Status202Accepted)
            .Produces<UploadResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return routes;
    }

    /// <summary>
    /// Corpo <c>multipart/form-data</c> con il campo binario <c>file</c> nel documento OpenAPI. Non si usa <c>Accepts</c>: aggiunge
    /// una regola di instradamento che risponderebbe 415 senza corpo ai contenuti non multipart, mentre l'endpoint li rifiuta
    /// da solo con un <c>ProblemDetails</c> uniforme.
    /// </summary>
    public static OpenApiOptions AddUploadRequestBody(this OpenApiOptions options) =>
        options.AddOperationTransformer(async (operation, context, ct) =>
        {
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().Any(m => m.EndpointName == OperationName))
            {
                var schema = await context.GetOrCreateSchemaAsync(typeof(UploadForm), cancellationToken: ct);
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType> { ["multipart/form-data"] = new() { Schema = schema } },
                };
            }
        });

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        ClaimsPrincipal user,
        [FromServices] IngestionDbContext db,
        [FromServices] IOutbox outbox,
        [FromServices] IEnumerable<IActivityFileParser> parsers,
        [FromServices] IOptions<UploadOptions> options,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LoggerCategory);
        var ct = http.RequestAborted;
        try
        {
            return await ProcessAsync(http, user, db, outbox, parsers, options.Value, logger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);   // client disconnesso: nessuno legge la risposta
        }
        catch (Exception ex)
        {
            // Solo il tipo: il messaggio e lo stack di un'eccezione possono contenere dati (ad esempio i valori di una chiave).
            logger.LogError("Errore inatteso durante l'upload ({ExceptionType}).", ex.GetType().Name);
            return UploadProblems.Internal();
        }
    }

    private static async Task<IResult> ProcessAsync(
        HttpContext http,
        ClaimsPrincipal user,
        IngestionDbContext db,
        IOutbox outbox,
        IEnumerable<IActivityFileParser> parsers,
        UploadOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        // L'utente solo dal principal autenticato (DEC-006): mai da form, query o header.
        Guid userId;
        try
        {
            userId = user.GetUserId();
        }
        catch (InvalidOperationException)
        {
            return Results.Unauthorized();
        }

        // ---- Lettura e validazioni, nell'ordine: campo file (400), estensione (415), dimensione (413), vuoto (400).
        var maxBody = options.MaxBytes + MultipartOverheadBytes;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = maxBody;   // tetto del server, in piu' di quello del lettore
        }

        var form = await MultipartUploadReader.ReadAsync(http.Request, options.MaxBytes, maxBody, ct);
        switch (form.Failure)
        {
            case UploadReadFailure.NotMultipart:
                return UploadProblems.InvalidField();
            case UploadReadFailure.Malformed:
                return UploadProblems.Malformed();
            case UploadReadFailure.BodyTooLarge:
                return UploadProblems.TooLarge(options.MaxBytes);
        }

        if (form.FileFields != 1 || !form.FileFieldHasFileName)
        {
            return UploadProblems.InvalidField();
        }

        var cleanName = UploadFileName.Sanitize(form.RawFileName);
        var parser = parsers.FirstOrDefault(p => p.CanParse(cleanName));
        if (parser is null)
        {
            return UploadProblems.UnsupportedType();
        }

        if (form.FileTooLarge || form.Content is null)
        {
            return UploadProblems.TooLarge(options.MaxBytes);
        }

        if (!form.Content.TryGetBuffer(out var content) || content.Count == 0)
        {
            return form.Content.Length == 0 ? UploadProblems.Empty() : UploadProblems.Internal();
        }

        // ---- Deduplicazione per contenuto, prima di spendere tempo nel parsing (REQ-011).
        var externalId = Convert.ToHexStringLower(SHA256.HashData(content));
        var existing = await FindUploadAsync(db, userId, externalId, ct);
        if (existing is { } known)
        {
            return Duplicate(logger, known, externalId);
        }

        // ---- Parsing con tempo massimo (REQ-010).
        var parsed = await ParseAsync(parser, content, options.ParseTimeoutSeconds, ct);
        if (parsed.Failure is { } failure)
        {
            return failure;
        }

        // ---- Evento e registro, in una sola SaveChanges (REQ-012).
        var activity = parsed.Activity!;
        var now = DateTimeOffset.UtcNow;
        var envelope = new EventEnvelope<ActivityImported>(
            Guid.NewGuid(),
            EventNames.ActivityImported,
            Version: 1,
            now,
            userId,
            RequireCorrelationId(http),
            new ActivityImported(
                Source,
                externalId,
                activity.Sport,
                activity.StartTime,
                activity.ElapsedSeconds,
                activity.MovingSeconds,
                activity.DistanceMeters,
                activity.ElevationGainMeters,
                activity.Summary,
                activity.Laps,
                activity.Stream));

        // Rete di sicurezza (REQ-036): i tetti dei parser lo rendono impossibile, ma un evento che NATS rifiuterebbe
        // resterebbe per sempre nell'outbox.
        if (SerializedLength(envelope) >= EventSizeLimitBytes)
        {
            return UploadProblems.EventTooBig();
        }

        var upload = new Upload
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Source = Source,
            ExternalId = externalId,
            FileName = UploadFileName.Truncate(cleanName),
            SizeBytes = content.Count,
            CreatedAt = now,
        };
        db.Uploads.Add(upload);
        outbox.Add(EventNames.ActivityImportedSubject, envelope);

        try
        {
            // Senza il token della richiesta: una disconnessione a meta' non deve lasciare l'esito ambiguo per il client.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex) when (IsDuplicateUpload(ex))
        {
            // Un upload concorrente dello stesso file ha vinto: la transazione e' annullata per intero (anche l'outbox).
            db.ChangeTracker.Clear();
            var winner = await FindUploadAsync(db, userId, externalId, ct)
                ?? throw new InvalidOperationException("Duplicato segnalato dal database ma riga non trovata.");
            return Duplicate(logger, winner, externalId);
        }

        logger.LogInformation("Upload accettato ({UploadId}, {SizeBytes} byte).", upload.Id, upload.SizeBytes);
        return Results.Json(new UploadResponse(upload.Id, externalId, UploadResponse.Accepted), statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Duplicate(ILogger logger, Guid uploadId, string externalId)
    {
        logger.LogInformation("Upload duplicato ({UploadId}).", uploadId);
        return Results.Json(new UploadResponse(uploadId, externalId, UploadResponse.Duplicate), statusCode: StatusCodes.Status200OK);
    }

    private static Task<Guid?> FindUploadAsync(IngestionDbContext db, Guid userId, string externalId, CancellationToken ct) =>
        db.Uploads.AsNoTracking()
            .Where(u => u.UserId == userId && u.Source == Source && u.ExternalId == externalId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);

    private static bool IsDuplicateUpload(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IngestionDbContext.UploadsUniqueIndexName,
        };

    /// <summary>
    /// Il <c>correlationId</c> della richiesta (middleware di ServiceDefaults). Se manca e' un errore di cablaggio (500), non
    /// un valore da inventare: l'evento perderebbe il legame con la richiesta.
    /// </summary>
    private static Guid RequireCorrelationId(HttpContext http) =>
        http.GetCorrelationId() is var id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("correlationId assente: manca il middleware di ServiceDefaults.");

    private sealed record ParseOutcome(ParsedActivity? Activity, IResult? Failure);

    /// <summary>
    /// Esegue il parser su un thread del pool con un tetto di tempo. Se il parser non rispetta il token la richiesta viene
    /// comunque liberata allo scadere (<c>WaitAsync</c>): il lavoro orfano finisce da solo e il suo esito e' ignorato.
    /// </summary>
    private static async Task<ParseOutcome> ParseAsync(IActivityFileParser parser, ArraySegment<byte> content, int timeoutSeconds, CancellationToken requestAborted)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var work = Task.Run(() => parser.Parse(new MemoryStream(content.Array!, content.Offset, content.Count, writable: false), limit.Token), CancellationToken.None);
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            return new ParseOutcome(await work.WaitAsync(limit.Token), null);
        }
        catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
        {
            throw;   // client disconnesso: lo gestisce il chiamante in silenzio
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return new ParseOutcome(null, UploadProblems.Timeout(timeoutSeconds));
        }
        catch (TooManyPointsException)
        {
            return new ParseOutcome(null, UploadProblems.TooManyPointsInFile());
        }
        catch (ActivityFileException)
        {
            // Il messaggio dei parser e' sicuro, ma non si riporta: la risposta e' generica.
            return new ParseOutcome(null, UploadProblems.InvalidActivity());
        }
    }

    /// <summary>Lunghezza in byte del JSON dell'evento (con le opzioni del contratto), senza tenerlo tutto in memoria.</summary>
    private static long SerializedLength(EventEnvelope<ActivityImported> envelope)
    {
        var counter = new CountingBufferWriter();
        using (var writer = new Utf8JsonWriter(counter))
        {
            JsonSerializer.Serialize(writer, envelope, ContractJson.Options);
        }

        return counter.Length;
    }

    /// <summary>Scarta i byte ma li conta: serve solo a misurare.</summary>
    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _scratch = new byte[64 * 1024];

        public long Length { get; private set; }

        public void Advance(int count) => Length += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Buffer(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Buffer(sizeHint).Span;

        private Memory<byte> Buffer(int sizeHint)
        {
            // Una singola scrittura del serializzatore non supera il buffer richiesto: si adegua se serve di piu'.
            var memory = _scratch.AsMemory();
            return sizeHint <= memory.Length ? memory : new byte[sizeHint];
        }
    }
}
