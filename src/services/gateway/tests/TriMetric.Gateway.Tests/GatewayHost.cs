using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TriMetric.Gateway.Tests;

/// <summary>
/// L'host reale di <c>TriMetric.Gateway</c> (<c>Program</c>) su Kestrel vero (non TestServer: il server di prova non applica i
/// limiti del corpo), con gli indirizzi dei cluster e la porta sovrascritti.
/// </summary>
/// <remarks>
/// Le impostazioni passano da <c>UseSetting</c> (visibili gia' alla costruzione dell'host): <c>ConfigureAppConfiguration</c> e'
/// applicato DOPO e sarebbe ignorato in silenzio per Kestrel, YARP e le opzioni lette da <c>Program</c> mentre registra i servizi.
/// </remarks>
internal sealed class GatewayHost(
    string? ingestionAddress = null,
    string? activitiesAddress = null,
    IReadOnlyDictionary<string, string?>? settings = null,
    ILoggerProvider? logs = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var all = new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
            ["ReverseProxy:Clusters:ingestion:Destinations:primary:Address"] = ingestionAddress ?? ClosedPort.Address(),
            ["ReverseProxy:Clusters:activities:Destinations:primary:Address"] = activitiesAddress ?? ClosedPort.Address(),
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        foreach (var (key, value) in all)
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (logs is not null)
            {
                logging.AddProvider(logs);
            }
        });
    }

    /// <summary>Avvia Kestrel e restituisce gli indirizzi su cui ascolta (il primo e' quello usato dai client).</summary>
    public IReadOnlyList<string> StartAndGetAddresses()
    {
        UseKestrel();
        StartServer();
        return [.. Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses];
    }

    /// <summary>Avvia Kestrel e restituisce un client (senza proxy di sistema) con l'indirizzo base del gateway.</summary>
    public HttpClient StartClient(TimeSpan? timeout = null) => new(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri(StartAndGetAddresses()[0]),
        Timeout = timeout ?? TimeSpan.FromSeconds(60),
    };

    /// <summary>Indirizzo del gateway dopo l'avvio.</summary>
    public string Address => Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}

/// <summary>Un indirizzo loopback su cui nessuno ascolta (connessione rifiutata subito): "backend giu'".</summary>
internal static class ClosedPort
{
    public static string Address()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }
}

/// <summary>Provider di log in memoria (messaggi, scopi ed eccezioni in un'unica stringa per voce).</summary>
internal sealed class InMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class InMemoryLogger(InMemoryLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scopes = new List<string>();
            owner._scopes.ForEachScope(
                (scope, list) => list.Add(scope is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : scope?.ToString() ?? string.Empty),
                scopes);
            owner._entries.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {string.Join(' ', scopes)} {exception}");
        }
    }
}

/// <summary>Posizione dei file del servizio nel repository (i test leggono i file di configurazione reali).</summary>
internal static class ServicePaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ProjectDirectory { get; } = Path.Combine(
        RepositoryRoot, "src", "services", "gateway", "src", "TriMetric.Gateway");

    public static IReadOnlyList<string> AppSettingsFiles() =>
        Directory.GetFiles(ProjectDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);

    public static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("global.json non trovato risalendo dalla cartella dei binari.");
    }
}

/// <summary>Attese con polling e timeout (mai attese fisse lunghe).</summary>
internal static class Eventually
{
    public static async Task AssertAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), $"Condizione non raggiunta entro {timeout.TotalSeconds:0.#} s: {description}");
    }
}
