using System.Globalization;
using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Metrica.Contracts;

namespace Metrica.IntegrationTests.Support;

/// <summary>Messaggio letto dallo stream: sequenza, subject, intestazioni, dimensione sul server e (se richiesto) payload.</summary>
internal sealed record StreamMessage(ulong Sequence, string Subject, IReadOnlyDictionary<string, string> Headers, int SizeBytes, byte[] Data)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>Busta dell'evento (richiede il payload).</summary>
    public EventEnvelope<ActivityImported> Envelope =>
        JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(Data, ContractJson.Options)!;
}

/// <summary>
/// Letture dal SERVER NATS (non dal codice sotto test): contenuto degli stream <c>EVENTS</c> e <c>DLQ</c> e consumatore
/// durevole. Ogni lettura usa un consumatore effimero di sola lettura (nessuna conferma), quindi non altera lo stato.
/// </summary>
internal sealed class NatsProbe(INatsJSContext js)
{
    public const string ActivitiesDurable = "activities-activity-imported";

    public static readonly string DlqSubject = $"dlq.{ActivitiesDurable}";

    public INatsJSContext Js { get; } = js;

    /// <summary>Messaggi dello stream sul subject (o tutti), in ordine di sequenza. Con <paramref name="withPayload"/> falso il server manda solo le intestazioni e la dimensione.</summary>
    public async Task<IReadOnlyList<StreamMessage>> ReadAsync(string stream, string? subject, bool withPayload)
    {
        var consumer = await Js.CreateOrUpdateConsumerAsync(
            stream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = subject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.None,
                HeadersOnly = !withPayload,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        var pending = (int)consumer.Info.NumPending;
        var messages = new List<StreamMessage>();
        if (pending == 0)
        {
            return messages;
        }

        await foreach (var message in consumer.FetchAsync<byte[]>(
            new NatsJSFetchOpts { MaxMsgs = pending, Expires = TimeSpan.FromSeconds(20) },
            NatsRawSerializer<byte[]>.Default))
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (message.Headers is { } h)
            {
                foreach (var (key, value) in h)
                {
                    headers[key] = value.ToString();
                }
            }

            // Con le sole intestazioni il server dichiara la dimensione del messaggio vero in Nats-Msg-Size.
            var size = withPayload
                ? message.Data?.Length ?? 0
                : headers.TryGetValue("Nats-Msg-Size", out var declared) ? int.Parse(declared, CultureInfo.InvariantCulture) : -1;
            messages.Add(new StreamMessage(message.Metadata!.Value.Sequence.Stream, message.Subject, headers, size, message.Data ?? []));
            if (messages.Count >= pending)
            {
                break;
            }
        }

        return messages;
    }

    public Task<IReadOnlyList<StreamMessage>> EventsAsync(bool withPayload = true) =>
        ReadAsync(EventNames.EventsStream, EventNames.ActivityImportedSubject, withPayload);

    public Task<IReadOnlyList<StreamMessage>> DlqAsync(bool withPayload = true) =>
        ReadAsync(EventNames.DlqStream, DlqSubject, withPayload);

    /// <summary>Numero di messaggi dello stream sul subject, dalle informazioni del SERVER (nessun consumatore).</summary>
    public async Task<int> CountAsync(string stream, string subject)
    {
        var info = await Js.GetStreamAsync(stream, new StreamInfoRequest { SubjectsFilter = subject });
        return info.Info.State.Subjects is { } subjects && subjects.TryGetValue(subject, out var count) ? (int)count : 0;
    }

    public Task<int> EventsCountAsync() => CountAsync(EventNames.EventsStream, EventNames.ActivityImportedSubject);

    public Task<int> DlqCountAsync() => CountAsync(EventNames.DlqStream, DlqSubject);

    public async Task<ConsumerInfo?> DurableAsync()
    {
        try
        {
            return (await Js.GetConsumerAsync(EventNames.EventsStream, ActivitiesDurable)).Info;
        }
        catch (NatsJSApiException)
        {
            return null;
        }
    }

    /// <summary>Il consumatore di activities ha elaborato (o confermato) tutto: nulla in attesa ne' non confermato.</summary>
    public async Task<bool> DurableDrainedAsync() => await DurableAsync() is { NumPending: 0, NumAckPending: 0 };

    /// <summary>Pubblica un payload sul subject reale degli eventi (come farebbe ingestion).</summary>
    public async Task PublishEventAsync(byte[] payload, string? msgId = null)
    {
        var ack = await Js.PublishAsync(
            EventNames.ActivityImportedSubject,
            payload,
            NatsRawSerializer<byte[]>.Default,
            msgId is null ? null : new NatsJSPubOpts { MsgId = msgId });
        if (ack.Error is { } error)
        {
            throw new NatsJSApiException(error);
        }
    }

    public Task PublishEventAsync(EventEnvelope<ActivityImported> envelope, string? msgId = null) =>
        PublishEventAsync(JsonSerializer.SerializeToUtf8Bytes(envelope, ContractJson.Options), msgId);

    /// <summary>Riporta NATS a uno stato pulito tra gli scenari: nessun consumatore durevole, <c>EVENTS</c> e <c>DLQ</c> vuoti.</summary>
    public async Task ResetAsync()
    {
        try
        {
            await Js.DeleteConsumerAsync(EventNames.EventsStream, ActivitiesDurable);
        }
        catch (NatsJSApiException)
        {
            // Non esiste ancora.
        }

        await Js.PurgeStreamAsync(EventNames.EventsStream, new StreamPurgeRequest());
        await Js.PurgeStreamAsync(EventNames.DlqStream, new StreamPurgeRequest());
    }
}
