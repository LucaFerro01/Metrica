using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TriMetric.Gateway.Tests;

/// <summary>
/// DEC-009: il gateway ascolta solo su loopback. Si avvia l'host reale (Kestrel) con la configurazione del progetto, cambiando
/// soltanto la porta (5080 non si usa nei test), e si verifica che nessuna interfaccia non loopback accetti connessioni.
/// </summary>
public sealed class BindingTests
{
    [Fact]
    public async Task RealHost_WithTheProjectEndpoint_ListensOnLoopbackOnly()
    {
        // Stesso host "localhost" del file di configurazione, ma una porta libera effimera invece di 5080 (Kestrel non accetta
        // "localhost:0"). Tra la scelta della porta e l'avvio c'e' una finestra minima in cui un altro processo potrebbe occuparla.
        var listenPort = new Uri(ClosedPort.Address()).Port;
        await using var host = new GatewayHost(settings: new Dictionary<string, string?> { ["Kestrel:Endpoints:Http:Url"] = $"http://localhost:{listenPort}" });

        var addresses = host.StartAndGetAddresses();

        Assert.NotEmpty(addresses);
        var ports = new HashSet<int>();
        foreach (var address in addresses)
        {
            var uri = new Uri(address);
            var isLoopback = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
            Assert.True(isLoopback, $"indirizzo di ascolto non loopback: {address}");
            Assert.NotEqual("0.0.0.0", uri.Host);
            Assert.NotEqual("[::]", uri.Host);
            ports.Add(uri.Port);
        }

        // Prova effettiva: ogni indirizzo non loopback della macchina rifiuta la connessione sulle porte del gateway.
        var nonLoopback = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .Where(a => !IPAddress.IsLoopback(a) && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal))
            .ToList();
        foreach (var address in nonLoopback)
        {
            foreach (var port in ports)
            {
                using var client = new TcpClient(address.AddressFamily);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var connected = false;
                try
                {
                    await client.ConnectAsync(address, port, timeout.Token);
                    connected = true;
                }
                catch (Exception error) when (error is SocketException or OperationCanceledException)
                {
                    // atteso: nessuno ascolta su questa interfaccia
                }

                Assert.False(connected, $"il gateway accetta connessioni su {address}:{port}");
            }
        }
    }

    [Fact]
    public async Task RealHost_AnswersOnLoopback()
    {
        await using var host = new GatewayHost();
        using var client = host.StartClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
