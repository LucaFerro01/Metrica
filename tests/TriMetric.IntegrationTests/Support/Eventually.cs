namespace TriMetric.IntegrationTests.Support;

/// <summary>Attese con polling e timeout (mai attese fisse per sincronizzare): al timeout falliscono dicendo cosa mancava.</summary>
internal static class Eventually
{
    /// <summary>Timeout generoso per i flussi che passano da outbox, NATS e consumatore (di norma bastano pochi secondi).</summary>
    public static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(30);

    /// <summary>Ripete <paramref name="probe"/> finche <paramref name="done"/> e' vero e restituisce l'ultimo valore.</summary>
    public static async Task<T> ValueAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> done,
        TimeSpan timeout,
        string description,
        Func<T, string>? describe = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        T last;
        while (true)
        {
            last = await probe();
            if (done(last))
            {
                return last;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(50);
        }

        var seen = describe is null ? last?.ToString() : describe(last);
        Assert.Fail($"Condizione non raggiunta entro {timeout.TotalSeconds:0.#} s: {description}. Ultimo valore osservato: {seen}");
        return last;
    }

    public static Task AssertAsync(Func<Task<bool>> condition, TimeSpan timeout, string description) =>
        ValueAsync(condition, ok => ok, timeout, description);
}
