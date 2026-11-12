using System.Text.Json;
using System.Text.Json.Serialization;

namespace TriMetric.Contracts;

/// <summary>
/// Opzioni JSON condivise da produttori e consumatori degli eventi.
/// L'evento versione 1 e' <see cref="ActivityImported"/> con <c>EventEnvelope.Version = 1</c>.
/// </summary>
public static class ContractJson
{
    /// <summary>
    /// Proprieta' in camelCase, enum come stringhe camelCase (<c>Sport.Run</c> diventa "run"),
    /// <c>null</c> sempre scritti (le colonne assenti restano <c>null</c> esplicito), nessuna indentazione.
    /// </summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
