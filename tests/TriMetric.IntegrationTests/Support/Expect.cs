namespace TriMetric.IntegrationTests.Support;

/// <summary>Confronti numerici con tolleranza e messaggio che dice cosa si confrontava.</summary>
internal static class Expect
{
    /// <summary>Verifica lo stato HTTP e, se diverso, mostra il corpo della risposta (il motivo dell'errore).</summary>
    public static void Status(System.Net.HttpStatusCode expected, ApiResponse response) =>
        Assert.True(response.Status == expected, $"atteso {(int)expected} {expected}, ricevuto {(int)response.Status} {response.Status}: {response.Body}");

    public static void Close(double expected, double? actual, double absoluteTolerance, string what)
    {
        Assert.True(actual is not null, $"{what}: atteso {expected}, il sistema non ha restituito un valore");
        Assert.True(
            Math.Abs(expected - actual!.Value) <= absoluteTolerance,
            $"{what}: atteso {expected} (tolleranza {absoluteTolerance}), ottenuto {actual}");
    }

    /// <summary>Tolleranza relativa (utile per la distanza: il raggio terrestre scelto dal servizio differisce da quello del test).</summary>
    public static void CloseRelative(double expected, double? actual, double relativeTolerance, string what) =>
        Close(expected, actual, Math.Abs(expected) * relativeTolerance, what);

    public static void SameInstant(DateTimeOffset expected, DateTimeOffset actual, string what) =>
        Assert.True(
            Math.Abs((expected - actual).TotalMilliseconds) < 1,
            $"{what}: atteso {expected:O}, ottenuto {actual:O}");
}
