using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TriMetric.Contracts;

namespace TriMetric.Activities;

/// <summary>
/// Salva <see cref="Sport"/> come testo minuscolo (<c>run|ride|swim|other</c>, DEC-005), la stessa rappresentazione di
/// JSON, API e OpenAPI. Il convertitore predefinito di EF (<c>HasConversion&lt;string&gt;</c>) scriverebbe "Run": qui la
/// corrispondenza e' esplicita e un valore sconosciuto (in scrittura o in lettura) e' un errore chiaro, mai un valore
/// predefinito silenzioso.
/// </summary>
public sealed class SportConverter() : ValueConverter<Sport, string>(sport => ToText(sport), text => FromText(text))
{
    private const string Allowed = "run, ride, swim, other";

    public static string ToText(Sport sport) => sport switch
    {
        Sport.Run => "run",
        Sport.Ride => "ride",
        Sport.Swim => "swim",
        Sport.Other => "other",
        _ => throw new ArgumentOutOfRangeException(
            nameof(sport), (int)sport, $"Valore di Sport non valido ({(int)sport}): ammessi {Allowed}."),
    };

    public static Sport FromText(string text) => text switch
    {
        "run" => Sport.Run,
        "ride" => Sport.Ride,
        "swim" => Sport.Swim,
        "other" => Sport.Other,
        _ => throw new InvalidOperationException($"Valore di sport sconosciuto nel database: '{text}' (ammessi: {Allowed})."),
    };
}
