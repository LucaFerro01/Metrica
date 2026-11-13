namespace TriMetric.Messaging;

/// <summary>Opzioni del relay dell'outbox, lette dalla sezione di configurazione <c>Outbox</c>.</summary>
public sealed class OutboxRelayOptions
{
    /// <summary>Nome della sezione di configurazione.</summary>
    public const string SectionName = "Outbox";

    /// <summary>Pausa tra due letture dell'outbox quando non c'e' altro da fare (<c>Outbox:PollIntervalMs</c>).</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Numero massimo di messaggi letti e pubblicati in una transazione (<c>Outbox:BatchSize</c>).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Tempo massimo di attesa dell'ack di JetStream per un messaggio (<c>Outbox:PublishTimeoutSeconds</c>).</summary>
    public int PublishTimeoutSeconds { get; set; } = 10;

    /// <summary>Pausa massima del backoff esponenziale in caso di errori (<c>Outbox:MaxBackoffSeconds</c>).</summary>
    public int MaxBackoffSeconds { get; set; } = 30;

    internal bool IsValid() =>
        PollIntervalMs >= 1 && BatchSize >= 1 && PublishTimeoutSeconds >= 1 && MaxBackoffSeconds >= 1;
}
