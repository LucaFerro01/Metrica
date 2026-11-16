using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace TriMetric.IntegrationTests.Hosting;

/// <summary>Una richiesta cosi come il servizio la riceve: metodo, percorso con query e intestazioni.</summary>
internal sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Registro delle richieste ricevute da un servizio (per verificare cosa il gateway gli ha davvero inoltrato).</summary>
internal sealed class RequestRecorder
{
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new();

    public IReadOnlyList<ReceivedRequest> All => [.. _requests];

    public void Add(ReceivedRequest request) => _requests.Enqueue(request);

    /// <summary>Richieste ricevute dopo le prime <paramref name="mark"/> (vedi <see cref="Count"/>).</summary>
    public IReadOnlyList<ReceivedRequest> Since(int mark) => [.. _requests.Skip(mark)];

    public int Count => _requests.Count;
}

/// <summary>Middleware in testa alla pipeline del servizio: registra la richiesta e passa oltre, senza altro effetto.</summary>
internal sealed class RecordingStartupFilter(RequestRecorder recorder) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            recorder.Add(new ReceivedRequest(context.Request.Method, context.Request.Path + context.Request.QueryString, headers));
            await nextMiddleware(context);
        });
        next(app);
    };
}
