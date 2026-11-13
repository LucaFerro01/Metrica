using Microsoft.AspNetCore.Http;

namespace TriMetric.ServiceDefaults;

public static class CorrelationIdExtensions
{
    /// <summary>Chiave di <c>HttpContext.Items</c> con il <c>correlationId</c> corrente (un <see cref="Guid"/>).</summary>
    public const string ItemKey = "correlationId";

    /// <summary>
    /// <c>correlationId</c> della richiesta corrente: quello dell'header <c>X-Correlation-Id</c> se e' un UUID valido e non
    /// vuoto, altrimenti quello generato dal middleware; coincide con l'header della risposta e con lo scopo di log.
    /// Restituisce <see cref="Guid.Empty"/> se il middleware non e' nella pipeline: il chiamante lo tratta come errore.
    /// </summary>
    public static Guid GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(ItemKey, out var value) && value is Guid id ? id : Guid.Empty;
    }
}
