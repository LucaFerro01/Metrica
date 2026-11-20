using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using TriMetric.Contracts;
using TriMetric.ServiceDefaults;

namespace TriMetric.Activities;

/// <summary>
/// API di lettura delle attivita' (T13; REQ-018, REQ-019, REQ-021, REQ-022; dietro al gateway <c>/api/activities</c>):
/// <list type="bullet">
/// <item><c>GET /activities?sport=&amp;externalId=&amp;page=1&amp;pageSize=20</c> → <see cref="ActivityListPage"/> (operationId <c>ListActivities</c>);</item>
/// <item><c>GET /activities/{id}?maxPoints=1000</c> → <see cref="ActivityDetail"/> (operationId <c>GetActivity</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Identita'.</b> Ogni endpoint richiede l'autenticazione e l'utente viene SOLO dal <see cref="ClaimsPrincipal"/>
/// (DEC-006): nessun parametro, intestazione o corpo lo fornisce; ogni query filtra per <c>user_id</c>. Un'attivita' di un altro
/// utente risponde 404 esattamente come un id inesistente.</para>
/// <para><b>Parametri.</b> Si leggono come testo e si validano qui: un valore fuori intervallo, non numerico, vuoto o ripetuto
/// risponde 400 <c>ProblemDetails</c> (mai una correzione silenziosa), con lo stesso formato in ogni ambiente. Nel documento
/// OpenAPI hanno i tipi veri (interi con minimo, massimo e predefinito; <c>sport</c> come enum <see cref="Sport"/>;
/// <c>externalId</c> come stringa con <c>maxLength</c> 128) grazie a <see cref="AddActivityApiDocument"/>.</para>
/// <para><b>Filtro <c>externalId</c> (DEC-017).</b> Corrispondenza esatta e case-sensitive (uguaglianza SQL con parametro, mai
/// <c>LIKE</c>: <c>%</c> e <c>_</c> non sono jolly) tra le sole attivita' dell'utente del principal, combinabile con
/// <c>sport</c>, <c>page</c> e <c>pageSize</c>; <c>total</c> conta le attivita' che rispettano tutti i filtri. Assente = nessun
/// filtro; vuoto, ripetuto, piu' lungo di <see cref="MaxExternalIdLength"/> caratteri o con caratteri di controllo o spazi = 400.
/// Il valore ricevuto non si riporta mai nella risposta ne' nei log.</para>
/// <para>Nessun log (REQ-034); query EF parametrizzate e <c>AsNoTracking</c>; elenco = una query per la pagina e una per il totale.</para>
/// </remarks>
public static class ActivityEndpoints
{
    public const string ListRoute = "/activities";
    public const string DetailRoute = "/activities/{id:guid}";
    public const string ListOperation = "ListActivities";
    public const string DetailOperation = "GetActivity";

    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>
    /// Lunghezza massima di <c>externalId</c> in caratteri (unita' UTF-16 di <see cref="string.Length"/>).
    /// </summary>
    /// <remarks>
    /// Volutamente piu' stretta dei limiti dell'importer (<see cref="ActivityImportedValidation.MaxKeyLength"/> = 256, che accetta
    /// anche spazi): l'unica fonte oggi, <c>file</c>, produce lo SHA-256 esadecimale del contenuto, 64 caratteri senza spazi, e il
    /// filtro serve a ritrovare proprio quegli identificativi. Un identificativo piu' lungo o con spazi (una fonte futura) puo'
    /// essere memorizzato ma NON e' raggiungibile con il filtro, che risponde 400. Se arriva un'altra fonte i due limiti vanno
    /// riallineati (DEC-017).
    /// </remarks>
    public const int MaxExternalIdLength = 128;

    private const string TypePrefix = "urn:trimetric:problem:";

    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(ListRoute, ListAsync)
            .RequireAuthorization()
            .WithName(ListOperation)
            .WithTags("Activities")
            .WithSummary("Elenco paginato delle attività dell'utente, dalla più recente.")
            .Produces<ActivityListPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        routes.MapGet(DetailRoute, GetAsync)
            .RequireAuthorization()
            .WithName(DetailOperation)
            .WithTags("Activities")
            .WithSummary("Dettaglio di un'attività con lo stream ridotto.")
            .Produces<ActivityDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// Completa il documento OpenAPI per il client generato (DEC-007): schemi dei parametri di query, che gli handler leggono come
    /// testo (<c>page</c> e <c>pageSize</c> interi con limiti e predefiniti, <c>sport</c> riferito allo schema <see cref="Sport"/>,
    /// <c>externalId</c> stringa opzionale con <c>maxLength</c> pari a <see cref="MaxExternalIdLength"/>, <c>maxPoints</c> intero con il massimo configurato), e <c>type: string</c> sullo schema <see cref="Sport"/> (enum minuscolo).
    /// </summary>
    public static OpenApiOptions AddActivityApiDocument(this OpenApiOptions options)
    {
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(Sport))
            {
                schema.Type = JsonSchemaType.String;
            }

            return Task.CompletedTask;
        });

        return options.AddOperationTransformer((operation, context, _) =>
        {
            var name = context.Description.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName;
            if (name is not (ListOperation or DetailOperation) || operation.Parameters is null)
            {
                return Task.CompletedTask;
            }

            var maxStream = context.ApplicationServices.GetRequiredService<IOptions<ActivitiesApiOptions>>().Value.MaxStreamPoints;
            foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
            {
                parameter.Schema = parameter.Name switch
                {
                    "page" => Integer(1, null, DefaultPage),
                    "pageSize" => Integer(1, MaxPageSize, DefaultPageSize),
                    "maxPoints" => Integer(ActivitiesApiOptions.MinMaxPoints, maxStream, ActivitiesApiOptions.DefaultMaxPoints),
                    "sport" => new OpenApiSchemaReference(nameof(Sport), context.Document),
                    "externalId" => new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = MaxExternalIdLength },
                    _ => parameter.Schema,
                };
            }

            return Task.CompletedTask;
        });
    }

    private static IOpenApiSchema Integer(int minimum, int? maximum, int defaultValue) => new OpenApiSchema
    {
        Type = JsonSchemaType.Integer,
        Format = "int32",
        Minimum = minimum.ToString(CultureInfo.InvariantCulture),
        Maximum = maximum?.ToString(CultureInfo.InvariantCulture),
        Default = JsonValue.Create(defaultValue),
    };

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user,
        [FromQuery] string? sport,
        [FromQuery] string? externalId,
        [FromQuery] string? page,
        [FromQuery] string? pageSize,
        HttpRequest request,
        [FromServices] ActivitiesDbContext db,
        CancellationToken ct)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return Results.Unauthorized();
        }

        if (!TryParseInt(page, DefaultPage, 1, int.MaxValue, out var pageNumber))
        {
            return InvalidQuery("page", "deve essere un intero maggiore o uguale a 1.");
        }

        if (!TryParseInt(pageSize, DefaultPageSize, 1, MaxPageSize, out var size))
        {
            return InvalidQuery("pageSize", $"deve essere un intero tra 1 e {MaxPageSize}.");
        }

        Sport? sportFilter = null;
        if (sport is not null)
        {
            if (!TryParseSport(sport, out var parsed))
            {
                return InvalidQuery("sport", "deve essere uno tra run, ride, swim, other (minuscolo).");
            }

            sportFilter = parsed;
        }

        // externalId: un valore ripetuto arriva come "a,b" nel parametro letto come testo, ma una virgola e' un carattere
        // ammesso: si conta quindi sulla query grezza. Il valore non entra mai nel messaggio di errore.
        if (externalId is not null && (request.Query["externalId"].Count != 1 || !IsValidExternalId(externalId)))
        {
            return InvalidQuery(
                "externalId",
                $"deve essere un testo di al massimo {MaxExternalIdLength} caratteri, senza spazi né caratteri di controllo, indicato una sola volta.");
        }

        // L'utente viene SEMPRE dal principal e il vincolo e' nella query di base: ogni filtro successivo lo restringe soltanto.
        var query = db.Activities.AsNoTracking().Where(a => a.UserId == userId);
        if (sportFilter is { } s)
        {
            query = query.Where(a => a.Sport == s);
        }

        if (externalId is not null)
        {
            // Uguaglianza esatta con parametro (case-sensitive con le regole di confronto del database): mai LIKE.
            query = query.Where(a => a.ExternalId == externalId);
        }

        var total = await query.CountAsync(ct);

        // Oltre la fine (anche oltre int.MaxValue righe saltate) la pagina e' vuota: nessun overflow, nessuna query.
        var skip = (long)(pageNumber - 1) * size;
        IReadOnlyList<ActivityListItem> items = skip >= total
            ? []
            : await query
                .OrderByDescending(a => a.StartTime)
                .ThenBy(a => a.Id)
                .Skip((int)skip)
                .Take(size)
                .Select(a => new ActivityListItem(
                    a.Id, a.ExternalId, a.Sport, a.StartTime, a.DistanceMeters, a.ElapsedSeconds, a.ElevationGainMeters))
                .ToListAsync(ct);

        return TypedResults.Ok(new ActivityListPage(items, pageNumber, size, total));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        ClaimsPrincipal user,
        [FromQuery] string? maxPoints,
        [FromServices] ActivitiesDbContext db,
        [FromServices] ActivityStreamReader streamReader,
        [FromServices] IOptions<ActivitiesApiOptions> options,
        CancellationToken ct)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return Results.Unauthorized();
        }

        var maxStream = options.Value.MaxStreamPoints;
        if (!TryParseInt(maxPoints, ActivitiesApiOptions.DefaultMaxPoints, ActivitiesApiOptions.MinMaxPoints, maxStream, out var points))
        {
            return InvalidQuery("maxPoints", $"deve essere un intero tra {ActivitiesApiOptions.MinMaxPoints} e {maxStream}.");
        }

        // Proprietario nella stessa condizione dell'id: l'attivita' di un altro utente e' "non trovata", come una inesistente.
        var activity = await db.Activities.AsNoTracking()
            .Where(a => a.Id == id && a.UserId == userId)
            .FirstOrDefaultAsync(ct);
        if (activity is null)
        {
            return NotFound();
        }

        var laps = await db.Laps.AsNoTracking()
            .Where(l => l.ActivityId == activity.Id)
            .OrderBy(l => l.StartTime)
            .ThenBy(l => l.Id)
            .Select(l => new LapData(l.StartTime, l.ElapsedSeconds, l.DistanceMeters))
            .ToListAsync(ct);

        var stream = await streamReader.ReadAsync(activity.Id, activity.StartTime, points, ct);

        return TypedResults.Ok(new ActivityDetail(
            activity.Id,
            activity.ExternalId,
            activity.Sport,
            activity.StartTime,
            activity.DistanceMeters,
            activity.ElapsedSeconds,
            activity.ElevationGainMeters,
            activity.MovingSeconds,
            new ActivitySummary(activity.AvgHeartRate, activity.MaxHeartRate, activity.AvgPowerWatts, activity.AvgCadence),
            laps,
            stream));
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        try
        {
            userId = user.GetUserId();
            return true;
        }
        catch (InvalidOperationException)
        {
            userId = Guid.Empty;
            return false;
        }
    }

    /// <summary>Assente = predefinito; presente (anche vuoto o ripetuto, che arriva come "a,b") deve essere un intero nei limiti.</summary>
    private static bool TryParseInt(string? text, int defaultValue, int min, int max, out int value)
    {
        if (text is null)
        {
            value = defaultValue;
            return true;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    /// <summary>Solo i valori minuscoli del contratto (<c>run|ride|swim|other</c>): "Run", "0" o un valore sconosciuto sono 400.</summary>
    private static bool TryParseSport(string text, out Sport sport)
    {
        switch (text)
        {
            case "run": sport = Sport.Run; return true;
            case "ride": sport = Sport.Ride; return true;
            case "swim": sport = Sport.Swim; return true;
            case "other": sport = Sport.Other; return true;
            default: sport = default; return false;
        }
    }

    /// <summary>
    /// <c>externalId</c> valido: da 1 a <see cref="MaxExternalIdLength"/> caratteri, nessun carattere di controllo (anche C1) e
    /// nessuno spazio (compresi quelli Unicode). Nessuna normalizzazione: il valore si confronta cosi' com'e'.
    /// </summary>
    private static bool IsValidExternalId(string value)
    {
        if (value.Length is 0 or > MaxExternalIdLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>400 con <c>type</c> e <c>title</c> stabili; il <c>detail</c> nomina il parametro, mai il valore ricevuto.</summary>
    private static IResult InvalidQuery(string parameter, string rule) =>
        Results.Problem(
            type: TypePrefix + "invalid-query",
            title: "Parametro non valido",
            statusCode: StatusCodes.Status400BadRequest,
            detail: $"Il parametro '{parameter}' {rule}");

    private static IResult NotFound() =>
        Results.Problem(
            type: TypePrefix + "activity-not-found",
            title: "Attività non trovata",
            statusCode: StatusCodes.Status404NotFound,
            detail: "L'attività richiesta non esiste.");
}
