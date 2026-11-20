using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Metrica.ServiceDefaults.Tests;

/// <summary>Costruisce in processo un'app minima di prova (TestServer), senza progetto host separato.</summary>
internal static class TestApp
{
    public static readonly Guid DevUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static WebApplicationBuilder CreateBuilder(string environment, IDictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = "Metrica.ServiceDefaults.Tests",
        });
        builder.WebHost.UseTestServer();
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        return builder;
    }

    public static IDictionary<string, string?> WithDevUser(Guid? userId = null) =>
        new Dictionary<string, string?> { ["Dev:UserId"] = (userId ?? DevUserId).ToString() };

    /// <summary>App con ServiceDefaults e identita di sviluppo, con endpoint protetti e di log.</summary>
    public static async Task<WebApplication> StartAsync(
        string environment,
        IDictionary<string, string?>? configuration = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = CreateBuilder(environment, configuration);
        builder.AddServiceDefaults();
        builder.AddDevelopmentIdentity();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.MapServiceDefaults();
        app.MapGet("/me", (ClaimsPrincipal user) => user.GetUserId().ToString())
            .RequireAuthorization();
        app.MapPost("/me", (ClaimsPrincipal user) => user.GetUserId().ToString())
            .RequireAuthorization();
        app.MapGet("/open", () => "open");
        app.MapGet("/correlation", (HttpContext context, ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("Test").LogInformation("richiesta elaborata");
            return context.GetCorrelationId().ToString();
        });
        app.MapMethods("/log", ["GET", "POST"], (ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("Test").LogInformation("richiesta elaborata");
            return "ok";
        });
        await app.StartAsync();
        return app;
    }
}
