using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.Core;
using Xunit;

namespace TriMetric.TestInfrastructure;

/// <summary>
/// NATS con JetStream configurato dal file REALE <c>deploy/nats/nats-server.conf</c> (DEC-013).
/// La porta host e fissa: scelta una sola volta e riusata dopo <see cref="StopAsync"/>/<see cref="StartAsync"/>,
/// cosi un riavvio non cambia <see cref="Url"/> e i client possono riconnettersi.
/// I dati di JetStream restano nel filesystem del container tra stop e start.
/// </summary>
public sealed class NatsFixture : IAsyncLifetime
{
    public const string Image = "docker.io/library/nats:2";

    private const int ClientPort = 4222;
    private const int MonitorPort = 8222;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private IContainer? _container;

    /// <summary>Indirizzo del server, stabile per tutta la vita della fixture (anche dopo un riavvio).</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>Indirizzo HTTP del monitoraggio (<c>/varz</c>, <c>/healthz</c>).</summary>
    public string MonitorUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var (clientHostPort, monitorHostPort) = FreeTcpPortPair();
        Url = $"nats://127.0.0.1:{clientHostPort}";
        MonitorUrl = $"http://127.0.0.1:{monitorHostPort}";

        _container = new ContainerBuilder(Image)
            .WithCommand("-c", "/etc/nats/nats-server.conf")
            .WithResourceMapping(RepositoryRoot.File("deploy", "nats", "nats-server.conf"), "/etc/nats/")
            .WithPortBinding(clientHostPort, ClientPort)
            .WithPortBinding(monitorHostPort, MonitorPort)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();

        await _container.StartAsync();
        await WaitUntilReadyAsync();
    }

    /// <summary>Ferma il container (JetStream e dati restano nel suo filesystem).</summary>
    public Task StopAsync() => Container.StopAsync();

    /// <summary>Riavvia lo stesso container sulla stessa porta e attende che sia pronto.</summary>
    public async Task StartAsync()
    {
        await Container.StartAsync();
        await WaitUntilReadyAsync();
    }

    public Task DisposeAsync() => _container is null ? Task.CompletedTask : _container.DisposeAsync().AsTask();

    private IContainer Container => _container ?? throw new InvalidOperationException("Fixture non inizializzata.");

    /// <summary>
    /// Sceglie due porte libere diverse tenendo aperti entrambi i listener finche non sono state scelte tutte e due
    /// (altrimenti la seconda scelta potrebbe restituire la stessa porta appena rilasciata dalla prima).
    /// </summary>
    private static (int First, int Second) FreeTcpPortPair()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.Loopback, 0);
        first.Start();
        second.Start();
        try
        {
            return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
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
