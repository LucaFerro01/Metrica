using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TriMetric.TestInfrastructure;

namespace TriMetric.Ingestion.Tests;

/// <summary>
/// Il tetto del corpo e' del codice dell'endpoint, non di Kestrel: qui si usa il server di prova (TestServer), che non applica
/// alcun limite proprio, e quindi un tetto che non fosse in streaming farebbe leggere tutto.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Upload")]
public sealed class UploadBodyLimitTests(TimescaleFixture db, NatsFixture nats)
{
    private const long MaxBytes = 2048;
    private const long Margin = 1024 * 1024;   // overhead multipart consentito oltre MaxBytes

    private UploadApp Start() => UploadApp.Start(db, nats, new Dictionary<string, string?> { ["Upload:MaxBytes"] = MaxBytes.ToString() });

    /// <summary>Corpo multipart di dimensione arbitraria generato al volo: scrive la parte iniziale, poi riempimento, e conta i byte scritti.</summary>
    private sealed class GeneratedMultipart(string boundary, string headPart, long fillerBytes, string tailPart, bool declareLength) : HttpContent
    {
        private readonly byte[] _head = Encoding.UTF8.GetBytes(headPart);
        private readonly byte[] _tail = Encoding.UTF8.GetBytes(tailPart);
        private long _written;

        public long Written => Interlocked.Read(ref _written);

        public string Boundary { get; } = boundary;

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            try
            {
                await stream.WriteAsync(_head);
                Interlocked.Add(ref _written, _head.Length);
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'a');
                for (long sent = 0; sent < fillerBytes; sent += chunk.Length)
                {
                    await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, fillerBytes - sent)));
                    Interlocked.Add(ref _written, chunk.Length);
                }

                await stream.WriteAsync(_tail);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // il server ha smesso di leggere (rifiuto): scrittura interrotta, e' l'esito atteso
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declareLength ? _head.Length + fillerBytes + _tail.Length : -1;
            return declareLength;
        }
    }

    private static GeneratedMultipart Build(string head, long filler, string tail, bool declareLength)
    {
        var content = new GeneratedMultipart("limite", head, filler, tail, declareLength);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=limite");
        return content;
    }

    private const string FileHeader = "Content-Disposition: form-data; name=\"file\"; filename=\"a.gpx\"";

    [Fact]
    public async Task HugeNonFileField_Returns413_WhileTheClientIsStillSending()
    {
        await using var app = Start();
        var gate = new TaskCompletionSource();
        // Un campo di testo di 3 MiB (oltre MaxBytes + 1 MiB), poi il client si ferma e non chiude mai il corpo: senza un tetto in
        // streaming il server resterebbe in attesa del resto (e, con un corpo vero, lo leggerebbe tutto in memoria).
        using var content = new StallingMultipart(
            "--limite\r\nContent-Disposition: form-data; name=\"nota\"\r\n\r\n", 3L * 1024 * 1024, gate.Task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await app.Client.PostAsync("/uploads", content, timeout.Token);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.EndsWith("file-too-large", (await Reply.ReadAsync(response)).ProblemType);
            await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    /// <summary>Scrive l'inizio e un riempimento, poi attende il rilascio prima di concludere (senza <c>Content-Length</c>).</summary>
    private sealed class StallingMultipart : HttpContent
    {
        private readonly byte[] _head;
        private readonly long _filler;
        private readonly Task _gate;

        public StallingMultipart(string head, long filler, Task gate)
        {
            _head = Encoding.UTF8.GetBytes(head);
            _filler = filler;
            _gate = gate;
            Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=limite");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            try
            {
                await stream.WriteAsync(_head);
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'a');
                for (long sent = 0; sent < _filler; sent += chunk.Length)
                {
                    await stream.WriteAsync(chunk);
                }

                await _gate;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // il server ha smesso di leggere: esito atteso
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    [Fact]
    public async Task FileOverTheCap_WithoutDeclaredContentLength_Returns413()
    {
        await using var app = Start();
        var head = $"--limite\r\n{FileHeader}\r\n\r\n";
        using var content = Build(head, 16L * 1024 * 1024, "\r\n--limite--\r\n", declareLength: false);
        Assert.Null(content.Headers.ContentLength);

        using var response = await app.Client.PostAsync("/uploads", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
    }

    [Fact]
    public async Task HugeDeclaredContentLength_IsRejectedImmediately_WithoutWaitingForTheBody()
    {
        await using var app = Start();
        var release = new TaskCompletionSource();
        using var content = new NeverEndingContent(10L * 1024 * 1024 * 1024, release.Task);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=limite");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            using var response = await app.Client.PostAsync("/uploads", content, timeout.Token);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            await UploadDb.AssertNothingWrittenAsync(db, app.UserId);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>Dichiara una lunghezza enorme e non scrive nulla finche' non viene rilasciato.</summary>
    private sealed class NeverEndingContent(long declared, Task gate) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => await gate;

        protected override bool TryComputeLength(out long length)
        {
            length = declared;
            return true;
        }
    }
}
