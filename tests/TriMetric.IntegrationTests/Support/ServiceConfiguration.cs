using System.Globalization;
using System.Text.Json;
using TriMetric.TestInfrastructure;

namespace TriMetric.IntegrationTests.Support;

/// <summary>
/// Configurazione REALE di un servizio (<c>appsettings.json</c> e <c>appsettings.Development.json</c> del progetto), appiattita in
/// chiavi <c>A:B:C</c> per passare da <c>UseSetting</c>. Serve perche i tre servizi, nello stesso processo, hanno lo stesso
/// content root (la cartella dei binari: ogni <c>Program</c> lo fissa a <c>AppContext.BaseDirectory</c>) e i loro file omonimi
/// non possono stare tutti li: cosi ogni host riceve i propri valori reali (rotte YARP del gateway, limiti, filtri di log).
/// </summary>
internal static class ServiceConfiguration
{
    public static Dictionary<string, string> Load(string serviceFolder, string projectName)
    {
        var directory = Path.Combine(RepositoryRoot.Path, "src", "services", serviceFolder, "src", projectName);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Stesso ordine del framework in Development: il file dell'ambiente sovrascrive quello di base.
        foreach (var name in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                Flatten(document.RootElement, string.Empty, result);
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException($"Nessun appsettings trovato in {directory}.");
        }

        return result;
    }

    /// <summary>
    /// Nel file reale del gateway il cluster ha UNA sola destinazione e si chiama <c>primary</c>: l'override dei test ne sostituisce
    /// l'indirizzo. Con un'altra chiave (o piu' destinazioni) l'override ne aggiungerebbe una seconda.
    /// </summary>
    public static void AssertSinglePrimaryDestination(IReadOnlyDictionary<string, string> gateway, string cluster)
    {
        var prefix = $"ReverseProxy:Clusters:{cluster}:Destinations:";
        var destinations = gateway.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split(':')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (destinations.Count != 1 || !destinations[0].Equals("primary", StringComparison.OrdinalIgnoreCase)
            || !gateway.ContainsKey($"{prefix}primary:Address"))
        {
            throw new InvalidOperationException(
                $"Il file reale del gateway deve avere per il cluster '{cluster}' la sola destinazione 'primary' con 'Address' (trovate: {string.Join(", ", destinations)}).");
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", result);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index++.ToString(CultureInfo.InvariantCulture)}", result);
                }

                break;
            case JsonValueKind.String:
                result[prefix] = element.GetString()!;
                break;
            case JsonValueKind.True:
                result[prefix] = "True";
                break;
            case JsonValueKind.False:
                result[prefix] = "False";
                break;
            case JsonValueKind.Number:
                result[prefix] = element.GetRawText();
                break;
        }
    }
}
