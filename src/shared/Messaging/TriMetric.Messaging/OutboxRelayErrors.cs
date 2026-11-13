using System.Net.Sockets;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace TriMetric.Messaging;

/// <summary>Descrizione e classificazione degli errori di pubblicazione del relay.</summary>
internal static class OutboxRelayErrors
{
    /// <summary>
    /// Solo il nome del tipo dell'eccezione (e, per gli errori del server, codice e errCode): mai payload, subject, URL
    /// o testo dell'eccezione.
    /// </summary>
    public static string Describe(Exception exception) => exception is NatsJSApiException api
        ? $"{exception.GetType().Name} ({api.Error.Code}/{api.Error.ErrCode})"
        : exception.GetType().Name;

    /// <summary>
    /// <c>true</c> solo se il bus non risponde: timeout (anche quello del relay, <see cref="TimeoutException"/>) o errori di
    /// connessione/rete. In quel caso gli altri messaggi del batch farebbero la stessa fine, ognuno tenendo il lock
    /// per un timeout intero, quindi il batch si interrompe. <b>Qualsiasi altro errore</b> (risposta del server, stream
    /// assente, payload oltre <c>max_payload</c>, ...) riguarda quel messaggio: si registra e si passa al successivo,
    /// cosi' un messaggio problematico non blocca mai la coda.
    /// </summary>
    public static bool IsBusUnavailable(Exception exception) => exception is
        TimeoutException
        or IOException
        or SocketException
        or NatsTimeoutException
        or NatsJSTimeoutException
        or NatsNoReplyException
        or NatsConnectionFailedException
        or NatsJSConnectionException;
}
