using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Transforms;

namespace Metrica.Gateway;

/// <summary>
/// CONFINE DI SICUREZZA (REQ-021): cio' con cui il client dichiara chi e' (intestazioni di identita' e parametro di query
/// <c>userId</c>) non raggiunge mai i backend, che ricavano l'utente solo dal proprio principal. <c>Authorization</c> passa
/// invariata e il gateway non aggiunge mai identita'. Nessun valore rimosso viene registrato nei log (REQ-034).
/// </summary>
internal static class IdentityBoundary
{
    /// <summary>Intestazione del correlationId (la stessa di ServiceDefaults).</summary>
    public const string CorrelationIdHeader = "X-Correlation-Id";

    private const string UserIdQueryParameter = "userId";

    // Nomi esatti (alcuni sono gia' coperti da un prefisso: l'elenco resta per leggibilita' e come rete di sicurezza) ...
    private static readonly string[] IdentityHeaderNames =
    [
        "X-User-Id", "X-UserId", "X-User", "X-Forwarded-User", "X-Remote-User", "X-Authenticated-User", "X-Auth-User",
        "X-Identity", "X-Principal", "X-Claims",
        // ... e intestazioni di identita' di uso comune dei proxy di autenticazione.
        "X-Forwarded-Email", "X-Forwarded-Preferred-Username", "X-Forwarded-Groups", "Remote-User",
    ];

    // ... e prefissi: YARP non ha caratteri jolly, il confronto e' qui.
    private static readonly string[] IdentityHeaderPrefixes = ["X-User", "X-Auth", "X-Identity", "X-Claim", "X-Principal", "X-Authenticated"];

    public static bool IsIdentityHeader(string name) =>
        IdentityHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || IdentityHeaderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Trasformazione di codice registrata per TUTTE le rotte (anche quelle del file di configurazione, che non la possono
    /// disattivare): toglie le intestazioni di identita' dalla richiesta inoltrata e allinea il correlationId.
    /// </summary>
    public static void AddTransforms(Yarp.ReverseProxy.Transforms.Builder.TransformBuilderContext context) =>
        context.AddRequestTransform(request =>
        {
            RemoveIdentityHeaders(request.ProxyRequest);
            SetCorrelationId(request);
            return ValueTask.CompletedTask;
        });

    private static void RemoveIdentityHeaders(HttpRequestMessage request)
    {
        // Si raccolgono i nomi prima di rimuoverli: non si modifica la collezione mentre la si scorre.
        foreach (var name in request.Headers.Select(h => h.Key).Where(IsIdentityHeader).ToList())
        {
            request.Headers.Remove(name);
        }

        if (request.Content is { } content)
        {
            foreach (var name in content.Headers.Select(h => h.Key).Where(IsIdentityHeader).ToList())
            {
                content.Headers.Remove(name);
            }
        }
    }

    // Il correlationId valido (o generato) da ServiceDefaults e' quello che il client rivede nella risposta: il backend deve
    // usare lo stesso, altrimenti il client segnalerebbe un identificativo che nei log dei servizi non esiste.
    private static void SetCorrelationId(RequestTransformContext request)
    {
        // Si toglie SEMPRE il valore del client, anche se non ne esiste uno valido da sostituire.
        request.ProxyRequest.Headers.Remove(CorrelationIdHeader);

        var correlationId = ServiceDefaults.CorrelationIdExtensions.GetCorrelationId(request.HttpContext);
        if (correlationId == Guid.Empty)
        {
            return;
        }

        request.ProxyRequest.Headers.TryAddWithoutValidation(CorrelationIdHeader, correlationId.ToString());
    }

    /// <summary>
    /// Toglie dalla query ogni parametro <c>userId</c> (senza distinzione di maiuscole, anche ripetuto o con il nome codificato);
    /// gli altri parametri restano byte per byte come il client li ha inviati: nessuna ricodifica.
    /// </summary>
    public static QueryString RemoveUserIdParameter(QueryString query)
    {
        if (query.Value is not { Length: > 1 } raw)
        {
            return query;
        }

        var pairs = raw[1..].Split('&');
        var kept = pairs.Where(pair => !IsUserIdParameter(pair)).ToArray();
        if (kept.Length == pairs.Length)
        {
            return query;
        }

        // Se restano solo pezzi vuoti (es. "?&userId=1" -> "&") non si lascia un "?" residuo.
        return kept.All(pair => pair.Length == 0) ? QueryString.Empty : new QueryString("?" + string.Join('&', kept));
    }

    private static bool IsUserIdParameter(string pair)
    {
        var separator = pair.IndexOf('=');
        var name = separator < 0 ? pair : pair[..separator];
        return string.Equals(Uri.UnescapeDataString(name.Replace('+', ' ')), UserIdQueryParameter, StringComparison.OrdinalIgnoreCase);
    }
}
