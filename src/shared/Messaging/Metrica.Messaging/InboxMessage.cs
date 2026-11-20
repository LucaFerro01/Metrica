namespace Metrica.Messaging;

/// <summary>
/// Evento gia' elaborato da un consumatore (REQ-015). La riga e' scritta nella stessa transazione del gestore:
/// la chiave primaria <c>(Consumer, MessageId)</c> garantisce nel database che lo stesso evento non sia elaborato due volte.
/// </summary>
public sealed class InboxMessage
{
    /// <summary>Identificatore dell'evento (<c>EventEnvelope.Id</c>).</summary>
    public Guid MessageId { get; set; }

    /// <summary>Nome del consumatore durevole (<c>ConsumerOptions.DurableName</c>): lo stesso evento puo' essere elaborato da consumatori diversi.</summary>
    public required string Consumer { get; set; }

    /// <summary>Istante UTC in cui l'evento e' stato registrato (cioe' elaborato).</summary>
    public DateTimeOffset ProcessedAt { get; set; }
}
