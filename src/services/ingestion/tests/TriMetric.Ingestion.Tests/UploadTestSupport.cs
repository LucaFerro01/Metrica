using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using TriMetric.Contracts;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>Riga di <c>ingestion.uploads</c> letta con una connessione diretta del ruolo del servizio.</summary>
internal sealed record UploadRow(Guid Id, Guid UserId, string Source, string ExternalId, string FileName, long SizeBytes, DateTimeOffset CreatedAt);

/// <summary>Riga di <c>ingestion.outbox</c>.</summary>
internal sealed record OutboxRow(Guid Id, string Subject, string Type, byte[] Payload, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt)
{
    public EventEnvelope<ActivityImported> Envelope =>
        JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(Payload, ContractJson.Options)!;
}

/// <summary>Letture dirette del database (come <c>ingestion_svc</c>): i test non passano dall'host per verificare le scritture.</summary>
internal static class UploadDb
{
    public static async Task<IReadOnlyList<UploadRow>> UploadsAsync(TimescaleFixture db, Guid userId)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("ingestion"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, user_id, source, external_id, file_name, size_bytes, created_at FROM ingestion.uploads WHERE user_id = @u ORDER BY created_at, id",
            connection);
        command.Parameters.AddWithValue("u", userId);
        var rows = new List<UploadRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new UploadRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    /// <summary>
    /// Righe dell'outbox con <c>userId</c> nella busta (il campo sta nei primi byte del JSON: si legge solo quella fetta,
    /// cosi' un payload da 6 MB di un altro test non pesa sulla ricerca).
    /// </summary>
    public static async Task<IReadOnlyList<OutboxRow>> OutboxAsync(TimescaleFixture db, Guid userId)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("ingestion"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, subject, type, payload, created_at, published_at FROM ingestion.outbox "
            + "WHERE position(@needle IN substr(payload, 1, 400)) > 0 ORDER BY created_at, id",
            connection);
        command.Parameters.AddWithValue("needle", Encoding.UTF8.GetBytes($"\"userId\":\"{userId}\""));
        var rows = new List<OutboxRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new OutboxRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3],
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return rows;
    }

    public static async Task AssertNothingWrittenAsync(TimescaleFixture db, Guid userId)
    {
        Assert.Empty(await UploadsAsync(db, userId));
        Assert.Empty(await OutboxAsync(db, userId));
    }
}

/// <summary>Contenuti di prova. Ogni file ha un identificatore univoco per non scontrarsi con gli altri test.</summary>
internal static class UploadSamples
{
    /// <summary>
    /// Corsa breve: 5 punti ogni 10 s (40 s), latitudine +0,0003 gradi per punto, quota 100..104, FC 140..160 (media 150),
    /// cadenza 80. <paramref name="marker"/> finisce in <c>&lt;name&gt;</c> e rende il contenuto (e l'hash) univoco.
    /// </summary>
    public static byte[] Run(string? marker = null, string? lat0 = null)
    {
        var start = lat0 is null ? 45.0 : double.Parse(lat0, System.Globalization.CultureInfo.InvariantCulture);
        var points = Enumerable.Range(0, 5).Select(i => GpxBuilder.Pt(
            (start + i * 0.0003).ToString("F7", System.Globalization.CultureInfo.InvariantCulture),
            "7.5000000",
            GpxBuilder.T(i * 10),
            ele: (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            inner: GpxBuilder.Tpx((140 + i * 5).ToString(System.Globalization.CultureInfo.InvariantCulture), "80"))).ToArray();
        var body = $"<trk><name>{marker ?? Guid.NewGuid().ToString("N")}</name><type>running</type>{GpxBuilder.Seg(points)}</trk>";
        return Encoding.UTF8.GetBytes(GpxBuilder.Doc(body));
    }

    public static byte[] Ride() => FitSampleBuilder.Ride();

    public static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>Costruttori di richieste multipart (anche ostili) per l'endpoint.</summary>
internal static class UploadRequests
{
    public static MultipartFormDataContent Form(string fileName, byte[] content, IReadOnlyDictionary<string, string>? fields = null, string fieldName = "file")
    {
        var form = new MultipartFormDataContent();
        AddFile(form, fieldName, fileName, content);
        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
        {
            form.Add(new StringContent(value), key);
        }

        return form;
    }

    public static void AddFile(MultipartFormDataContent form, string fieldName, string fileName, byte[] content)
    {
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, fieldName, fileName);
    }

    /// <summary>Corpo multipart scritto a mano: permette intestazioni arbitrarie (nome file con caratteri di controllo).</summary>
    public static byte[] RawMultipart(string boundary, params (string Headers, byte[] Body)[] parts)
    {
        using var ms = new MemoryStream();
        foreach (var (headers, body) in parts)
        {
            ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\n{headers}\r\n\r\n"));
            ms.Write(body);
            ms.Write("\r\n"u8);
        }

        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));
        return ms.ToArray();
    }

    public static HttpContent RawContent(string boundary, byte[] body, bool chunked)
    {
        HttpContent content = chunked ? new ChunkedContent(body) : new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        return content;
    }

    /// <summary>Contenuto che non dichiara la lunghezza (trasferimento a blocchi, senza <c>Content-Length</c>).</summary>
    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4096)
            {
                await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(4096, bytes.Length - offset)));
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}

/// <summary>Risposta di errore o di esito dell'endpoint, gia' letta.</summary>
internal sealed record Reply(System.Net.HttpStatusCode Status, string Body, string? MediaType, string? CorrelationHeader)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public Guid UploadId => Json.GetProperty("uploadId").GetGuid();

    public string ResponseStatus => Json.GetProperty("status").GetString()!;

    public string ExternalId => Json.GetProperty("externalId").GetString()!;

    public string ProblemType => Json.GetProperty("type").GetString()!;

    public static async Task<Reply> ReadAsync(HttpResponseMessage response) =>
        new(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.FirstOrDefault() : null);
}

/// <summary>
/// Un host dell'applicazione con un utente proprio (<c>Dev:UserId</c> casuale: ogni test ha i suoi dati sul database
/// condiviso) e un client. I servizi si possono sostituire con <paramref name="configureServices"/>.
/// </summary>
internal sealed class UploadApp : IAsyncDisposable
{
    private readonly IngestionHost _root;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;

    private UploadApp(IngestionHost root, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, HttpClient client, Guid userId)
    {
        _root = root;
        _factory = factory;
        Client = client;
        UserId = userId;
    }

    public HttpClient Client { get; }

    public Guid UserId { get; }

    public IServiceProvider Services => _factory.Services;

    public static UploadApp Start(
        TimescaleFixture db,
        NatsFixture nats,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        Action<ILoggingBuilder>? configureLogging = null,
        Guid? userId = null)
    {
        var user = userId ?? Guid.NewGuid();
        var root = IngestionHost.Development(db, nats, settings, user);
        var factory = root.WithWebHostBuilder(builder =>
        {
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }

            if (configureLogging is not null)
            {
                builder.ConfigureLogging(configureLogging);
            }
        });
        // Senza redirect: il gestore dei redirect di Mvc.Testing copia (bufferizza) tutto il corpo prima di inviarlo, e i test
        // sul tetto del corpo devono vedere il corpo arrivare al server in streaming.
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return new UploadApp(root, factory, client, user);
    }

    public async Task<Reply> PostAsync(HttpContent content, string url = "/uploads", string? correlationId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        }

        using var response = await Client.SendAsync(request);
        return await Reply.ReadAsync(response);
    }

    public async Task<Reply> UploadAsync(string fileName, byte[] content, IReadOnlyDictionary<string, string>? fields = null, string? correlationId = null)
    {
        using var form = UploadRequests.Form(fileName, content, fields);
        return await PostAsync(form, correlationId: correlationId);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
        await _root.DisposeAsync();
    }
}

/// <summary>Cattura tutti i log dell'host (messaggio formattato ed eccezione intera) per i controlli sulla privacy.</summary>
internal sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    public IEnumerable<string> AllText => Entries.Select(e => e.Text);

    private sealed class CapturingLogger(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                text += " " + string.Join(' ', values.Select(v => $"{v.Key}={v.Value}"));
            }

            owner.Entries.Enqueue((category, logLevel, exception is null ? text : text + " " + exception));
        }
    }
}

/// <summary>Ricerche sullo stream reale <c>EVENTS</c> (condiviso da tutti i test: si cerca per <c>Nats-Msg-Id</c>).</summary>
internal static class EventsStream
{
    /// <summary>Sequenze dello stream dei messaggi con quel <c>Nats-Msg-Id</c> sul subject, solo con le intestazioni (nessun payload grande).</summary>
    public static async Task<IReadOnlyList<ulong>> SequencesOfAsync(INatsJSContext js, string subject, Guid msgId)
    {
        var consumer = await js.CreateOrUpdateConsumerAsync(
            EventNames.EventsStream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = subject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                AckPolicy = ConsumerConfigAckPolicy.None,
                HeadersOnly = true,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        var pending = (int)consumer.Info.NumPending;
        var found = new List<ulong>();
        if (pending == 0)
        {
            return found;
        }

        var seen = 0;
        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = pending, Expires = TimeSpan.FromSeconds(10) }))
        {
            seen++;
            if (msg.Headers is { } headers
                && headers.TryGetValue("Nats-Msg-Id", out var values)
                && values.ToString() == msgId.ToString())
            {
                found.Add(msg.Metadata!.Value.Sequence.Stream);
            }

            if (seen >= pending)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>Legge il payload completo del messaggio con quella sequenza.</summary>
    public static async Task<EventEnvelope<ActivityImported>> ReadAsync(INatsJSContext js, string subject, ulong sequence)
    {
        var consumer = await js.CreateOrUpdateConsumerAsync(
            EventNames.EventsStream,
            new ConsumerConfig
            {
                Name = $"probe-{Guid.NewGuid():N}",
                FilterSubject = subject,
                DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                OptStartSeq = sequence,
                AckPolicy = ConsumerConfigAckPolicy.None,
                InactiveThreshold = TimeSpan.FromSeconds(30),
            });

        await foreach (var msg in consumer.FetchAsync<byte[]>(new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(10) }))
        {
            Assert.Equal(sequence, msg.Metadata!.Value.Sequence.Stream);
            return JsonSerializer.Deserialize<EventEnvelope<ActivityImported>>(msg.Data!, ContractJson.Options)!;
        }

        throw new InvalidOperationException("Messaggio non trovato nello stream.");
    }
}

/// <summary>Sostituisce tutti i parser registrati con quello dato.</summary>
internal static class ParserOverrides
{
    public static Action<IServiceCollection> Replace(IActivityFileParser parser) => services =>
    {
        services.RemoveAll<IActivityFileParser>();
        services.AddSingleton(parser);
    };
}
