using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TriMetric.Messaging;

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
}
