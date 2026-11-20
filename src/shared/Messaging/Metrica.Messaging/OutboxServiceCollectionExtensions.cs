using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Metrica.Messaging;

/// <summary>Registrazione di <see cref="IOutbox"/>.</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registra <see cref="IOutbox"/> (scoped) sul <typeparamref name="TDbContext"/> del servizio: la scrittura
    /// avviene con il <c>SaveChanges</c> di quel contesto. Un solo contesto per servizio.
    /// </summary>
    public static IServiceCollection AddOutbox<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IOutboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IOutbox>(sp => new Outbox<TDbContext>(sp.GetRequiredService<TDbContext>()));
        return services;
    }

    /// <summary>
    /// Registra il relay in background (<see cref="OutboxRelay{TDbContext}"/>) e <see cref="OutboxRelayStats"/>.
    /// Le opzioni si leggono dalla sezione <c>Outbox</c> (<c>PollIntervalMs</c> 500, <c>BatchSize</c> 50,
    /// <c>PublishTimeoutSeconds</c> 10, <c>MaxBackoffSeconds</c> 30) e sono validate all'avvio.
    /// Richiede <c>AddNatsMessaging</c> e il <typeparamref name="TDbContext"/> registrato; un solo relay per servizio.
    /// </summary>
    public static IServiceCollection AddOutboxRelay<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext, IOutboxDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OutboxRelayOptions>()
            .BindConfiguration(OutboxRelayOptions.SectionName)
            .Validate(
                o => o.IsValid(),
                "Outbox: PollIntervalMs, BatchSize, PublishTimeoutSeconds e MaxBackoffSeconds devono essere almeno 1.")
            .ValidateOnStart();
        services.TryAddSingleton<OutboxRelayStats>();
        services.AddHostedService<OutboxRelay<TDbContext>>();
        return services;
    }
}
