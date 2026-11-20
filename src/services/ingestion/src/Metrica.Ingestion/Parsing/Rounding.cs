namespace Metrica.Ingestion.Parsing;

/// <summary>Arrotondamento dei valori dello stream (DEC-012): meta' verso l'alto in valore assoluto, mai -0.</summary>
internal static class Rounding
{
    public const int TimeDecimals = 3;
    public const int CoordinateDecimals = 7;
    public const int AltitudeDecimals = 1;
    public const int SpeedDecimals = 3;
    public const int SummaryDecimals = 3;

    public static double Round(double value, int decimals)
    {
        var r = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return r == 0 ? 0.0 : r; // r == 0 e' vero anche per -0.0: lo normalizza a +0.0
    }

    public static double? Round(double? value, int decimals) => value is { } v ? Round(v, decimals) : null;
}
