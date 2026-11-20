namespace Metrica.Messaging;

/// <summary>
/// Contatori del relay per diagnostica e test (thread-safe, solo letture pubbliche). Contano da quando il processo e' partito.
/// </summary>
public sealed class OutboxRelayStats
{
    private long _published;
    private long _duplicates;
    private long _failures;
    private long _cycles;

    /// <summary>Messaggi pubblicati e marcati nell'outbox (include quelli confermati come duplicati).</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>
    /// Sottoinsieme di <see cref="Published"/>: l'ack aveva <c>Duplicate = true</c> (stesso <c>Nats-Msg-Id</c> gia' accettato
    /// nella finestra di deduplicazione, ad esempio dopo un crash tra pubblicazione e marcatura).
    /// </summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>Pubblicazioni fallite (ack mancante, timeout, errore del server): il messaggio resta nell'outbox.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Cicli di lettura dell'outbox completati senza errori del database.</summary>
    public long Cycles => Interlocked.Read(ref _cycles);

    internal void AddPublished(int count, int duplicates)
    {
        Interlocked.Add(ref _published, count);
        Interlocked.Add(ref _duplicates, duplicates);
    }

    internal void AddFailure() => Interlocked.Increment(ref _failures);

    internal void AddCycle() => Interlocked.Increment(ref _cycles);
}
