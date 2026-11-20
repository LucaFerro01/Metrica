using System.Collections.Concurrent;

namespace Metrica.Messaging;

/// <summary>Contatori dei consumatori, per diagnostica e test (thread-safe, solo letture pubbliche), uno per nome durevole.</summary>
public sealed class EventConsumerStats
{
    private readonly ConcurrentDictionary<string, EventConsumerCounters> _counters = new(StringComparer.Ordinal);

    /// <summary>Contatori del consumatore <paramref name="durableName"/> (creati a zero se non esistono).</summary>
    public EventConsumerCounters For(string durableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(durableName);
        return _counters.GetOrAdd(durableName, _ => new EventConsumerCounters());
    }
}

/// <summary>Contatori di un consumatore, da quando il processo e' partito.</summary>
public sealed class EventConsumerCounters
{
    private long _received;
    private long _redelivered;
    private long _processed;
    private long _duplicates;
    private long _retried;
    private long _deadLettered;

    /// <summary>Messaggi ricevuti da JetStream (le riconsegne contano ogni volta).</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Sottoinsieme di <see cref="Received"/>: consegne successive alla prima (<c>NumDelivered</c> &gt; 1).</summary>
    public long Redelivered => Interlocked.Read(ref _redelivered);

    /// <summary>Eventi elaborati: transazione confermata (poi <c>Ack</c>).</summary>
    public long Processed => Interlocked.Read(ref _processed);

    /// <summary>Eventi gia' presenti nell'inbox: confermati senza eseguire il gestore.</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>Elaborazioni fallite che hanno prodotto un nuovo tentativo (<c>Nak</c> con attesa).</summary>
    public long Retried => Interlocked.Read(ref _retried);

    /// <summary>Messaggi pubblicati sulla DLQ.</summary>
    public long DeadLettered => Interlocked.Read(ref _deadLettered);

    internal void AddReceived(bool redelivery)
    {
        Interlocked.Increment(ref _received);
        if (redelivery)
        {
            Interlocked.Increment(ref _redelivered);
        }
    }

    internal void AddProcessed() => Interlocked.Increment(ref _processed);

    internal void AddDuplicate() => Interlocked.Increment(ref _duplicates);

    internal void AddRetried() => Interlocked.Increment(ref _retried);

    internal void AddDeadLettered() => Interlocked.Increment(ref _deadLettered);
}
