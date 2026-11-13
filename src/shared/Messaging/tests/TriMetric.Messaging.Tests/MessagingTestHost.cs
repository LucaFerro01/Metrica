using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;

namespace TriMetric.Messaging.Tests;

/// <summary>Host minimo con <c>AddNatsMessaging</c>, avviato e fermato dal test.</summary>
internal sealed class MessagingTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private MessagingTestHost(IHost host) => _host = host;

    public IServiceProvider Services => _host.Services;

    public INatsStreamsReadiness Readiness => Services.GetRequiredService<INatsStreamsReadiness>();

    public INatsJSContext JetStream => Services.GetRequiredService<INatsJSContext>();

    public NatsStreamsInitializer Initializer => Services.GetRequiredService<NatsStreamsInitializer>();

    /// <summary>Costruisce e avvia l'host; NON attende gli stream (lo fa il test con <c>WaitReadyAsync</c>).</summary>
    public static async Task<MessagingTestHost> StartAsync(string natsUrl, ILoggerProvider? loggerProvider = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (loggerProvider is not null)
        {
            builder.Logging.AddProvider(loggerProvider);
        }

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Nats:Url"] = natsUrl });
        builder.Services.AddNatsMessaging(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync();
        return new MessagingTestHost(host);
    }

    /// <summary>Attende gli stream con un timeout (mai attese fisse).</summary>
    public async Task WaitReadyAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        await Readiness.WhenReadyAsync(cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
