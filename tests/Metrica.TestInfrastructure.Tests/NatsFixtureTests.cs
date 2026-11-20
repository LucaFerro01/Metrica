using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Metrica.TestInfrastructure;
using Xunit;

namespace Metrica.TestInfrastructure.Tests;

/// <summary>Test di fumo sulla configurazione REALE <c>deploy/nats/nats-server.conf</c> (REQ-036, DEC-012).</summary>
public sealed class NatsFixtureTests(NatsFixture nats) : IClassFixture<NatsFixture>
{
    private const int EightMegabytes = 8 * 1024 * 1024;
    private const int SixMegabytes = 6 * 1024 * 1024;
    private const int NineMegabytes = 9 * 1024 * 1024;

    private NatsConnection Connect() => new(new NatsOpts { Url = nats.Url });

    private static async Task<(string Stream, string Subject)> CreateStreamAsync(NatsJSContext js)
    {
        var id = Guid.NewGuid().ToString("N");
        var stream = $"T_SMOKE_{id}";
        var subject = $"tsmoke.{id}";
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]));
        return (stream, subject);
    }

    [Fact]
    public void Fixture_UsesHostPortsInTheStableRange()
    {
        var client = new Uri(nats.Url).Port;
        var monitor = new Uri(nats.MonitorUrl).Port;

        Assert.NotEqual(client, monitor);
        Assert.InRange(client, FreePortAllocator.RangeStart, FreePortAllocator.RangeEnd);
        Assert.InRange(monitor, FreePortAllocator.RangeStart, FreePortAllocator.RangeEnd);
    }

    [Fact]
    public async Task EnsureRunning_WhenAlreadyRunning_DoesNothing_AndAfterStopRestartsOnTheSameUrl()
    {
        var url = nats.Url;

        await nats.EnsureRunningAsync();
        await nats.StopAsync();
        await nats.EnsureRunningAsync();

        Assert.Equal(url, nats.Url);
        await using var connection = Connect();
        await connection.ConnectAsync();
    }

    [Fact]
    public async Task WhileStopped_ThePortsStayReservedAgainstOtherProcesses()
    {
        var port = new Uri(nats.Url).Port;
        await nats.StopAsync();
        try
        {
            var other = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);

            Assert.Throws<System.Net.Sockets.SocketException>(other.Start);
        }
        finally
        {
            await nats.EnsureRunningAsync();
        }

        await using var connection = Connect();
        await connection.ConnectAsync();
    }

    [Fact]
    public async Task Server_AdvertisesEightMegabyteMaxPayload()
    {
        await using var connection = Connect();
        await connection.ConnectAsync();

        Assert.Equal(EightMegabytes, connection.ServerInfo?.MaxPayload);
    }

    [Fact]
    public async Task JetStream_IsEnabled()
    {
        await using var connection = Connect();
        var js = new NatsJSContext(connection);

        var account = await js.GetAccountInfoAsync();

        Assert.NotNull(account);
    }

    [Fact]
    public async Task JetStream_AcceptsSixMegabyteMessage()
    {
        await using var connection = Connect();
        var js = new NatsJSContext(connection);
        var (stream, subject) = await CreateStreamAsync(js);
        try
        {
            var ack = await js.PublishAsync(subject, new byte[SixMegabytes]);

            ack.EnsureSuccess();
            Assert.Equal(stream, ack.Stream);
        }
        finally
        {
            await js.DeleteStreamAsync(stream);
        }
    }

    [Fact]
    public async Task JetStream_RejectsNineMegabyteMessage()
    {
        await using var connection = Connect();
        var js = new NatsJSContext(connection);
        var (stream, subject) = await CreateStreamAsync(js);
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => js.PublishAsync(subject, new byte[NineMegabytes]).AsTask());

            Assert.IsType<NatsPayloadTooLargeException>(ex);
        }
        finally
        {
            await js.DeleteStreamAsync(stream);
        }
    }

    [Fact]
    public async Task Restart_KeepsUrlStreamAndPayloadLimit()
    {
        var urlBefore = nats.Url;
        string stream;
        string subject;
        await using (var before = Connect())
        {
            (stream, subject) = await CreateStreamAsync(new NatsJSContext(before));
        }

        await nats.StopAsync();
        await nats.StartAsync();

        Assert.Equal(urlBefore, nats.Url);
        await using var after = Connect();
        await after.ConnectAsync();
        Assert.Equal(EightMegabytes, after.ServerInfo?.MaxPayload);
        var js = new NatsJSContext(after);
        try
        {
            var existing = await js.GetStreamAsync(stream);
            Assert.Equal(stream, existing.Info.Config.Name);

            var ack = await js.PublishAsync(subject, new byte[SixMegabytes]);
            ack.EnsureSuccess();
        }
        finally
        {
            await js.DeleteStreamAsync(stream);
        }
    }
}
