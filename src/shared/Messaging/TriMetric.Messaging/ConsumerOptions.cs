using TriMetric.Contracts;

namespace TriMetric.Messaging;

/// <summary>Opzioni di un consumatore durevole (<see cref="EventConsumerServiceCollectionExtensions.AddEventConsumer{TDbContext, TData, THandler}"/>).</summary>
public sealed record ConsumerOptions
{
    /// <summary>Stream da cui si consuma.</summary>
    public string Stream { get; init; } = EventNames.EventsStream;

    /// <summary>Subject filtrato dal consumatore, ad esempio <c>ingestion.activity.imported</c>.</summary>
    public required string FilterSubject { get; init; }

    /// <summary>
    /// Nome del consumatore durevole, senza punti: e' anche il subject DLQ (<c>dlq.&lt;DurableName&gt;</c>) e il valore della
    /// colonna <c>consumer</c> dell'inbox.
    /// </summary>
    public required string DurableName { get; init; }

    /// <summary>Numero massimo di tentativi, il primo incluso (predefinito 5); il limite e' applicato dal consumatore, non dal server.</summary>
    public int MaxDeliver { get; init; } = 5;

    /// <summary>Attesa prima del nuovo tentativo: elemento <c>min(tentativo - 1, lunghezza - 1)</c> (predefinito 1 s, 2 s, 5 s, 10 s).</summary>
    public TimeSpan[] Backoff { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Tempo dopo il quale JetStream riconsegna un messaggio non confermato (predefinito 60 s); durante l'elaborazione il consumatore invia <c>AckProgress</c>.</summary>
    public TimeSpan AckWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Messaggi elaborati in parallelo (predefinito 1). E' anche il <c>MaxAckPending</c> del consumatore JetStream: i messaggi in
    /// attesa non sono ancora consegnati, quindi il loro <c>AckWait</c> non scorre.
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Lancia <see cref="ArgumentException"/> se le opzioni non sono valide (nomi vuoti o non ammessi da NATS, numeri non positivi).</summary>
    internal void Validate()
    {
        RequireToken(Stream, nameof(Stream), allowDots: false);
        RequireToken(DurableName, nameof(DurableName), allowDots: false);
        RequireToken(FilterSubject, nameof(FilterSubject), allowDots: true);

        if (MaxDeliver < 1)
        {
            throw new ArgumentException("MaxDeliver deve essere almeno 1.", nameof(MaxDeliver));
        }

        if (MaxConcurrency < 1)
        {
            throw new ArgumentException("MaxConcurrency deve essere almeno 1.", nameof(MaxConcurrency));
        }

        if (AckWait < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentException("AckWait deve essere almeno 100 ms.", nameof(AckWait));
        }

        if (Backoff is null || Backoff.Length == 0 || Backoff.Any(b => b <= TimeSpan.Zero))
        {
            throw new ArgumentException("Backoff deve contenere almeno un'attesa e tutte positive.", nameof(Backoff));
        }
    }

    private static void RequireToken(string? value, string name, bool allowDots)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} e' obbligatorio.", name);
        }

        // Spazi e caratteri jolly non sono ammessi nei nomi; i punti solo nei subject (nel nome durevole romperebbero dlq.<nome>).
        var forbidden = allowDots ? " \t\r\n*/\\" : " \t\r\n.*>/\\";
        if (value.IndexOfAny(forbidden.ToCharArray()) >= 0)
        {
            throw new ArgumentException($"{name} contiene caratteri non ammessi.", name);
        }
    }
}
