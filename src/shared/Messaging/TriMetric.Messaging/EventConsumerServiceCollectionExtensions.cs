using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;

namespace TriMetric.Messaging;

/// <summary>Registrazione dei consumatori di eventi.</summary>
public static class EventConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registra un consumatore durevole in background (<see cref="EventConsumer{TDbContext, TData, THandler}"/>) e il suo gestore
    /// (<typeparamref name="THandler"/>, scoped). Il gestore e l'inbox usano lo stesso <typeparamref name="TDbContext"/> e la stessa
    /// transazione. Richiede <c>AddNatsMessaging</c> e il <typeparamref name="TDbContext"/> registrato. Le opzioni sono validate subito
    /// (<see cref="ArgumentException"/>), cioe' all'avvio dell'applicazione; la rete non e' necessaria.
    /// </summary>
    public static IServiceCollection AddEventConsumer<TDbContext, TData, THandler>(
        this IServiceCollection services,
        ConsumerOptions options)
        where TDbContext : DbContext, IInboxDbContext
        where THandler : class, IEventHandler<TData>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // Copia: Backoff e' un array mutabile e il chiamante potrebbe modificarlo dopo la registrazione.
        var snapshot = options with { Backoff = [.. options.Backoff ?? []] };
        snapshot.Validate();

        services.TryAddSingleton<EventConsumerStats>();
        services.TryAddScoped<THandler>();
        services.AddSingleton<IHostedService>(sp => new EventConsumer<TDbContext, TData, THandler>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<INatsJSContext>(),
            sp.GetRequiredService<INatsStreamsReadiness>(),
            snapshot,
            sp.GetRequiredService<EventConsumerStats>(),
            sp.GetRequiredService<ILogger<EventConsumer<TDbContext, TData, THandler>>>()));
        return services;
    }
}
