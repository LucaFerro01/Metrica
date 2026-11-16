using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TriMetric.IntegrationTests.Hosting;

/// <summary>
/// Un servizio reale (il suo <c>Program</c>) in processo su Kestrel VERO con porta effimera (non <c>TestServer</c>, che non ha
/// porte ne' limiti del corpo), in ambiente Development come in sviluppo locale (le migrazioni si applicano all'avvio).
/// </summary>
/// <remarks>
/// Tutte le impostazioni passano da <c>UseSetting</c>: sono visibili gia' alla costruzione dell'host, mentre
/// <c>ConfigureAppConfiguration</c> arriva dopo e verrebbe ignorato in silenzio per le chiavi lette da <c>Program</c> mentre
/// registra i servizi (<c>Dev:UserId</c>, <c>Kestrel</c>, <c>Consumers:*</c>, i cluster YARP).
/// </remarks>
internal abstract class ServiceHost<TEntryPoint>(IReadOnlyDictionary<string, string> settings, ILoggerProvider logs)
    : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    /// <summary>Richieste ricevute dal servizio (osservazione passiva: stessa porta, stesso processo, nessuna modifica del comportamento).</summary>
    public RequestRecorder Requests { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new RecordingStartupFilter(Requests)));
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(logs);
            // Tutti i livelli: piu' messaggi catturati = controlli sulla privacy piu' severi.
            logging.SetMinimumLevel(LogLevel.Trace);
        });
    }

    /// <summary>Indirizzo reale (<c>http://127.0.0.1:porta</c>) su cui ascolta Kestrel, dopo <see cref="Launch"/>.</summary>
    public string Address { get; private set; } = string.Empty;

    /// <summary>Avvia Kestrel sulla porta effimera e ne legge l'indirizzo.</summary>
    public void Launch()
    {
        UseKestrel();
        StartServer();
        Address = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }
}
