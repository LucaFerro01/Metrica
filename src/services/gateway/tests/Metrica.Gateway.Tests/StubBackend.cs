using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Metrica.Gateway.Tests;

/// <summary>Una richiesta arrivata al server di prova: cio' che il backend vede davvero, byte per byte.</summary>
internal sealed class RecordedRequest
{
    private long _bodyBytes;
    private volatile bool _bodyCompleted;

    public required string Method { get; init; }

    /// <summary>Riga di richiesta come ricevuta (percorso e query non decodificati).</summary>
    public required string RawTarget { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    public long BodyBytes => Interlocked.Read(ref _bodyBytes);

    /// <summary>Il backend ha letto il corpo fino alla fine regolare (anche il fine-corpo del chunked).</summary>
    public bool BodyCompleted => _bodyCompleted;

    /// <summary>Corpo intero, solo se non supera 1 MiB.</summary>
    public byte[]? Body { get; internal set; }

    public string? BodySha256 { get; internal set; }

    /// <summary>Completato quando il gestore ha finito di leggere il corpo (anche se interrotto).</summary>
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IEnumerable<string> HeaderNames => Headers.Select(h => h.Key);

    public bool HasHeader(string name) => Headers.Any(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public string Path => RawTarget.Split('?', 2)[0];

    public string Query => RawTarget.Contains('?') ? "?" + RawTarget.Split('?', 2)[1] : string.Empty;

    internal void AddBytes(int count) => Interlocked.Add(ref _bodyBytes, count);

    internal void MarkCompleted() => _bodyCompleted = true;
}

/// <summary>
/// Server di prova su Kestrel vero (127.0.0.1, porta effimera) che registra ogni richiesta. Nessun limite sul corpo: i limiti
/// da provare sono quelli del gateway. Risponde 200 "ok" salvo diversa indicazione.
/// </summary>
internal sealed class StubBackend : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<RecordedRequest> _requests = [];

    private StubBackend(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static async Task<StubBackend> StartAsync(Func<HttpContext, RecordedRequest, Task>? respond = null)
    {
        var builder = CreateLoopbackBuilder();
        var app = builder.Build();
        StubBackend? stub = null;
        app.Run(context => HandleAsync(stub!, context, respond));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        stub = new StubBackend(app, address);
        return stub;
    }

    /// <summary>
    /// Builder vuoto su Kestrel (127.0.0.1, porta effimera, nessun limite sul corpo). Vuoto di proposito: i builder predefiniti
    /// leggono <c>appsettings.json</c> dalla cartella dei binari, dove c'e' quello del gateway (porta 5080), che i test non devono usare.
    /// </summary>
    public static WebApplicationBuilder CreateLoopbackBuilder()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Limits.MaxRequestBodySize = null;
        });
        builder.Services.AddRouting();
        return builder;
    }

    private static async Task HandleAsync(StubBackend stub, HttpContext context, Func<HttpContext, RecordedRequest, Task>? respond)
    {
        var record = new RecordedRequest
        {
            Method = context.Request.Method,
            RawTarget = context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget,
            Headers = [.. context.Request.Headers.SelectMany(h => h.Value.Select(v => KeyValuePair.Create(h.Key, v ?? string.Empty)))],
        };
        lock (stub._requests)
        {
            stub._requests.Add(record);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var small = new MemoryStream();
        var keepSmall = true;
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                record.AddBytes(read);
                if (keepSmall && small.Length + read <= 1024 * 1024)
                {
                    small.Write(buffer, 0, read);
                }
                else
                {
                    keepSmall = false;
                }
            }

            record.MarkCompleted();
        }
        catch (Exception)
        {
            // Corpo interrotto dal gateway (limite superato): il test lo verifica con BodyCompleted.
        }
        finally
        {
            record.BodySha256 = Convert.ToHexString(hash.GetHashAndReset());
            record.Body = keepSmall ? small.ToArray() : null;
            record.Finished.TrySetResult();
        }

        if (!record.BodyCompleted)
        {
            return;
        }

        if (respond is null)
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("ok");
        }
        else
        {
            await respond(context, record);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
