using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Metrica.Messaging;

/// <summary>Registrazione del client NATS e della creazione degli stream.</summary>
public static class NatsMessagingExtensions
{
    /// <summary>Chiave di configurazione dell'indirizzo del server.</summary>
    public const string UrlConfigurationKey = "Nats:Url";

    /// <summary>Indirizzo predefinito (sviluppo locale).</summary>
    public const string DefaultUrl = "nats://127.0.0.1:4222";

    /// <summary>
    /// Registra <see cref="INatsConnection"/> (singleton, riconnessione automatica), <see cref="INatsJSContext"/>
    /// e il servizio in background che crea gli stream <c>EVENTS</c> e <c>DLQ</c> senza bloccare l'avvio.
    /// L'indirizzo si legge da <c>Nats:Url</c> alla prima risoluzione della connessione. Le credenziali non vanno nell'URL.
    /// </summary>
    public static IServiceCollection AddNatsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // L'indirizzo si legge alla prima risoluzione (non alla registrazione) e preferibilmente dall'IConfiguration
        // del provider, cosi gli override aggiunti dopo (WebApplicationFactory nei test) vengono rispettati.
        services.TryAddSingleton<INatsConnection>(sp => new NatsConnection(new NatsOpts
        {
            Url = ResolveUrl(sp.GetService<IConfiguration>(), configuration),
            Name = "metrica",
            LoggerFactory = sp.GetRequiredService<ILoggerFactory>(),
            // Il client continua a riconnettersi finche il server non torna (REQ-013).
            MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(250),
            ReconnectWaitMax = TimeSpan.FromSeconds(5),
        }));
        services.TryAddSingleton<INatsJSContext>(sp => new NatsJSContext(sp.GetRequiredService<INatsConnection>()));

        services.TryAddSingleton<NatsStreamsInitializer>();
        services.TryAddSingleton<INatsStreamsReadiness>(sp => sp.GetRequiredService<NatsStreamsInitializer>());
        services.AddHostedService(sp => sp.GetRequiredService<NatsStreamsInitializer>());

        return services;
    }

    private static string ResolveUrl(IConfiguration? fromProvider, IConfiguration registered)
    {
        foreach (var source in new[] { fromProvider, registered })
        {
            var url = source?[UrlConfigurationKey];
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return DefaultUrl;
    }
}
