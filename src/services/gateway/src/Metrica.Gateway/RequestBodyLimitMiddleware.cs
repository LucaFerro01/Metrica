using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Metrica.Gateway;

/// <summary>
/// Rifiuta con 413 una richiesta il cui <c>Content-Length</c> dichiarato supera il limite, PRIMA di contattare il backend:
/// altrimenti YARP aprirebbe la richiesta verso il servizio e solo alla lettura del corpo Kestrel la interromperebbe. Il limite
/// di Kestrel (<see cref="GatewayOptions.MaxRequestBodyBytes"/>) resta la protezione per i corpi senza <c>Content-Length</c>.
/// </summary>
internal sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > options.Value.MaxRequestBodyBytes)
        {
            // Il corpo non verra' letto: si chiude la connessione invece di tenerla aperta in attesa di byte inutili.
            context.Response.Headers.Connection = "close";
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Richiesta troppo grande.",
                detail: "Il corpo della richiesta supera la dimensione massima consentita.")
                .ExecuteAsync(context);
        }

        return next(context);
    }
}
