using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Metrica.ServiceDefaults;

/// <summary>Impostazioni comuni a tutti i servizi: OpenTelemetry, health check, logging strutturato.</summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Tag dei controlli eseguiti da <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    private const string OtlpEndpointEnvironmentKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string OtlpEndpointConfigurationKey = "Otlp:Endpoint";

    /// <summary>Registra OpenTelemetry, health check e logging JSON con scopi (per il <c>correlationId</c>).</summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddLogging();
        builder.AddOpenTelemetryDefaults();

        // Il middleware del correlationId e il primo della pipeline, davanti ad autenticazione e routing:
        // copre ogni richiesta, anche quelle rifiutate con 401 o senza endpoint.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupFilter, CorrelationIdStartupFilter>());

        // `live` non registra alcun controllo; `ready` esegue quelli con tag `ready`, aggiunti dal servizio.
        builder.Services.AddHealthChecks();

        return builder;
    }

    /// <summary>Mappa <c>/health/live</c> e <c>/health/ready</c>.</summary>
    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
        });

        return app;
    }

    private static void AddLogging(this IHostApplicationBuilder builder)
    {
        // Solo console JSON: niente provider predefiniti dell'host (EventLog su Windows, Debug) con formati diversi.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

        // REQ-034: a livello Information il framework logga path e query string di ogni richiesta, che possono
        // contenere dati personali. Nessun logging di corpo o intestazioni (niente HttpLogging).
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
    }

    private static void AddOpenTelemetryDefaults(this IHostApplicationBuilder builder)
    {
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        // L'esportatore OTLP esiste solo se e configurato un endpoint: altrimenti nessun errore di rete.
        if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointEnvironmentKey]))
        {
            telemetry.UseOtlpExporter(); // legge OTEL_EXPORTER_OTLP_ENDPOINT (e protocollo) dalla configurazione
        }
        else if (builder.Configuration[OtlpEndpointConfigurationKey] is { Length: > 0 } configured)
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException(
                    $"La configurazione '{OtlpEndpointConfigurationKey}' non e un URI assoluto valido.");
            }

            telemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);
        }
    }
}
