using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using Xunit;

namespace Metrica.TestInfrastructure;

/// <summary>
/// NATS con JetStream configurato dal file REALE <c>deploy/nats/nats-server.conf</c> (DEC-013).
/// La porta host e fissa: scelta una sola volta in un intervallo non dinamico (<see cref="FreePortAllocator"/>) e riusata
/// dopo <see cref="StopAsync"/>/<see cref="StartAsync"/>, cosi un riavvio non cambia <see cref="Url"/> e i client possono
/// riconnettersi. Mentre il container e' fermo le due porte sono tenute occupate da listener locali (best effort) per
/// non farle prendere ad altri. I dati di JetStream restano nel filesystem del container tra stop e start.
/// </summary>
public sealed class NatsFixture : IAsyncLifetime
{
    public const string Image = "docker.io/library/nats:2";

    private const int ClientPort = 4222;
    private const int MonitorPort = 8222;
    private const int MaxStartAttempts = 3;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly string[] PortConflictMarkers =
    [
        "address already in use",
        "port is already allocated",
        "failed to bind host port",
        "ports are not available",
        "only one usage of each socket address",
    ];

    private IContainer? _container;
    private int _clientHostPort;
    private int _monitorHostPort;
    private List<TcpListener> _reservation = [];

    /// <summary>Indirizzo del server, stabile per tutta la vita della fixture (anche dopo un riavvio).</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>Indirizzo HTTP del monitoraggio (<c>/varz</c>, <c>/healthz</c>).</summary>
    public string MonitorUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Avvia il container. Se l'avvio fallisce perche la porta scelta e' stata presa nel frattempo da un altro processo,
    /// riprova (fino a 3 tentativi in tutto) con porte nuove: a questo punto l'indirizzo non e' ancora noto a nessuno.
    /// </summary>
    public async Task InitializeAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            var ports = FreePortAllocator.Allocate(2);
            _clientHostPort = ports[0];
            _monitorHostPort = ports[1];
            Url = $"nats://127.0.0.1:{_clientHostPort}";
            MonitorUrl = $"http://127.0.0.1:{_monitorHostPort}";
            _container = Build();

            try
            {
                await _container.StartAsync();
                await WaitUntilReadyAsync();
                return;
            }
            catch (Exception ex) when (attempt < MaxStartAttempts && IsPortConflict(ex))
            {
                await _container.DisposeAsync();
                _container = null;
                FreePortAllocator.Release(ports);
            }
        }
    }

    /// <summary>Ferma il container (JetStream e dati restano nel suo filesystem) e tiene occupate le porte.</summary>
    public async Task StopAsync()
    {
        await Container.StopAsync();
        await ReservePortsAsync();
    }

    /// <summary>
    /// Riavvia lo stesso container sulla stessa porta e attende che sia pronto. Se la porta e' stata presa da un altro
    /// processo mentre il container era fermo lancia un'eccezione con un messaggio chiaro (e quella originale come inner).
    /// </summary>
    public async Task StartAsync()
    {
        ReleaseReservation();
        try
        {
            await Container.StartAsync();
        }
        catch (Exception ex) when (IsPortConflict(ex))
        {
            throw new InvalidOperationException(
                $"NATS non riavviabile sulla stessa porta ({_clientHostPort}/{_monitorHostPort}): occupata da un altro processo dopo lo stop. {ex.Message}",
                ex);
        }

        await WaitUntilReadyAsync();
    }

    /// <summary>
    /// Garantisce che il server sia in esecuzione: non fa nulla se lo e' gia, altrimenti lo riavvia (e un errore di
    /// riavvio viene riportato con l'eccezione originale, mai ingoiato).
    /// </summary>
    public async Task EnsureRunningAsync()
    {
        if (Container.State == TestcontainersStates.Running)
        {
            return;
        }

        await StartAsync();
    }

    public Task DisposeAsync()
    {
        ReleaseReservation();
        FreePortAllocator.Release([_clientHostPort, _monitorHostPort]);
        return _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();
    }

    private IContainer Container => _container ?? throw new InvalidOperationException("Fixture non inizializzata.");

    private IContainer Build() => new ContainerBuilder(Image)
        .WithCommand("-c", "/etc/nats/nats-server.conf")
        .WithResourceMapping(RepositoryRoot.File("deploy", "nats", "nats-server.conf"), "/etc/nats/")
        .WithPortBinding(_clientHostPort, ClientPort)
        .WithPortBinding(_monitorHostPort, MonitorPort)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
        .Build();

    private static bool IsPortConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (PortConflictMarkers.Any(m => current.Message.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Occupa le due porte con listener locali finche il container e' fermo. Docker puo' impiegare un istante a
    /// liberarle dopo lo stop, quindi si riprova per qualche secondo; se non riesce la fixture funziona comunque
    /// (la protezione e' solo un'attenuazione).
    /// </summary>
    private async Task ReservePortsAsync()
    {
        ReleaseReservation();
        foreach (var port in new[] { _clientHostPort, _monitorHostPort })
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                    _reservation.Add(listener);
                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(100);
                }
            }
        }
    }

    private void ReleaseReservation()
    {
        foreach (var listener in _reservation)
        {
            listener.Stop();
        }

        _reservation = [];
    }

    /// <summary>Attende <c>/healthz</c> (include JetStream) e una connessione client riuscita, con polling e timeout.</summary>
    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var health = await Http.GetAsync($"{MonitorUrl}/healthz");
                if (health.IsSuccessStatusCode)
                {
                    await using var connection = new NatsConnection(new NatsOpts { Url = Url });
                    await connection.ConnectAsync();
                    return;
                }

                last = new InvalidOperationException($"/healthz ha risposto {(int)health.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NatsException or IOException or SocketException)
            {
                last = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"NATS non pronto su {Url} entro {ReadyTimeout.TotalSeconds:0} s.", last);
    }
}
