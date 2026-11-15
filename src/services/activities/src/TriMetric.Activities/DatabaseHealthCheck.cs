using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TriMetric.Activities;

/// <summary>
/// Controllo di <c>/health/ready</c>: il database del servizio e' raggiungibile. Nessun controllo di NATS (REQ-013: il
/// servizio e' pronto anche con il bus fermo). Il risultato non espone eccezioni o stringhe di connessione.
/// </summary>
internal sealed class DatabaseHealthCheck(ActivitiesDbContext context) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext healthContext, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);

        try
        {
            return await context.Database.CanConnectAsync(limit.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database non raggiungibile");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("database non raggiungibile (timeout)");
        }
    }
}
