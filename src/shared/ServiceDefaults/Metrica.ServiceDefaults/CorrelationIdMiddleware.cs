using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Metrica.ServiceDefaults;

/// <summary>
/// Accetta l'header <c>X-Correlation-Id</c> se e un UUID valido e non vuoto, altrimenti ne genera uno; lo restituisce nella
/// risposta, lo apre come scopo di log (<c>correlationId</c>) per tutta la richiesta e lo espone al codice dell'applicazione
/// con <see cref="CorrelationIdExtensions.GetCorrelationId"/> (<c>HttpContext.Items</c>).
/// </summary>
internal sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Guid.TryParse(context.Request.Headers[HeaderName].ToString(), out var incoming) && incoming != Guid.Empty
            ? incoming
            : Guid.NewGuid();
        var value = correlationId.ToString();
        context.Items[CorrelationIdExtensions.ItemKey] = correlationId;

        context.Response.OnStarting(
            static state =>
            {
                var (response, id) = ((HttpResponse, string))state;
                response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context.Response, value));

        using (logger.BeginScope(new Dictionary<string, object> { ["correlationId"] = value }))
        {
            await next(context);
        }
    }
}

/// <summary>Inserisce <see cref="CorrelationIdMiddleware"/> all'inizio della pipeline dell'applicazione.</summary>
internal sealed class CorrelationIdStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        next(app);
    };
}
