namespace Metrica.Messaging;

/// <summary>
/// Decide la pausa del relay dopo ogni ciclo: <c>PollIntervalMs</c> con coda vuota, nessuna pausa con un batch pieno e
/// senza errori, backoff esponenziale quando un ciclo non fa progressi per errori (o per un errore del database).
/// Il primo ciclo riuscito riporta tutto a <c>PollIntervalMs</c>.
/// </summary>
internal sealed class RelayPacer(int pollIntervalMs, int batchSize, int maxBackoffSeconds)
{
    /// <summary>Cicli consecutivi senza progressi (errori di pubblicazione o del database).</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Pausa dopo un batch che ha letto <paramref name="read"/> messaggi.</summary>
    public TimeSpan AfterBatch(int read, int published, int failed)
    {
        if (failed > 0 && published == 0)
        {
            ConsecutiveFailures++;
            return Backoff(ConsecutiveFailures, pollIntervalMs, maxBackoffSeconds);
        }

        ConsecutiveFailures = 0;

        // Batch pieno e senza errori: probabilmente c'e' altro da pubblicare. Con errori parziali si attende il polling
        // (un messaggio problematico non deve trasformare il relay in un ciclo continuo).
        return read >= batchSize && failed == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(pollIntervalMs);
    }

    /// <summary>Pausa dopo un errore del database (stesso backoff).</summary>
    public TimeSpan AfterDatabaseError()
    {
        ConsecutiveFailures++;
        return Backoff(ConsecutiveFailures, pollIntervalMs, maxBackoffSeconds);
    }

    /// <summary>
    /// <c>PollIntervalMs</c> raddoppiato a ogni fallimento consecutivo (il primo fallimento da' 2x), con tetto a
    /// <c>MaxBackoffSeconds</c> (mai sotto <c>PollIntervalMs</c>).
    /// </summary>
    internal static TimeSpan Backoff(int consecutiveFailures, int pollIntervalMs, int maxBackoffSeconds)
    {
        var max = Math.Max(maxBackoffSeconds * 1000d, pollIntervalMs);
        var exponent = Math.Clamp(consecutiveFailures, 0, 30);
        return TimeSpan.FromMilliseconds(Math.Min(pollIntervalMs * Math.Pow(2, exponent), max));
    }
}
