namespace Metrica.Messaging;

/// <summary>
/// Evento in attesa di essere pubblicato su JetStream, scritto nella stessa transazione dei dati di dominio (REQ-012).
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Identificatore dell'evento (<c>EventEnvelope.Id</c>): e' anche il <c>Nats-Msg-Id</c> della pubblicazione.</summary>
    public Guid Id { get; set; }

    /// <summary>Subject di destinazione, ad esempio <c>ingestion.activity.imported</c>.</summary>
    public required string Subject { get; set; }

    /// <summary>Nome dell'evento (<c>EventEnvelope.Type</c>).</summary>
    public required string Type { get; set; }

    /// <summary>Busta serializzata in JSON UTF-8 con <c>ContractJson.Options</c>.</summary>
    public required byte[] Payload { get; set; }

    /// <summary>Istante UTC di scrittura nell'outbox (ordine di pubblicazione).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Istante UTC della pubblicazione confermata; <c>null</c> finche non e' stato pubblicato.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Numero di tentativi di pubblicazione falliti.</summary>
    public int Attempts { get; set; }

    /// <summary>Ultimo errore di pubblicazione (mai dati personali).</summary>
    public string? LastError { get; set; }
}
