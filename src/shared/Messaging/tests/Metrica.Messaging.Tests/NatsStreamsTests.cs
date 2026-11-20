using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Metrica.Contracts;
using Metrica.TestInfrastructure;
using Xunit;

namespace Metrica.Messaging.Tests;

/// <summary>Creazione, idempotenza e non sovrapposizione degli stream EVENTS e DLQ (REQ-013, REQ-036, DEC-005).</summary>
[Collection(NatsCollection.Name)]
[Trait("Category", "Setup")]
public sealed class NatsStreamsTests(NatsFixture nats)
{
    private const int SixMegabytes = 6 * 1024 * 1024;

    [Fact]
    public async Task Streams_AreCreatedWithExpectedConfiguration()
    {
        await using var app = await MessagingTestHost.StartAsync(nats.Url);
        await app.WaitReadyAsync();

        var events = await app.JetStream.GetStreamAsync(EventNames.EventsStream);
        var dlq = await app.JetStream.GetStreamAsync(EventNames.DlqStream);

        Assert.Equal(["identity.>", "ingestion.>", "activities.>", "analytics.>", "training-plan.>"],
            events.Info.Config.Subjects);
        Assert.Equal(StreamConfigStorage.File, events.Info.Config.Storage);
        Assert.Equal(TimeSpan.FromHours(1), events.Info.Config.DuplicateWindow);
        Assert.Equal(-1, events.Info.Config.MaxMsgSize);

        Assert.Equal(["dlq.>"], dlq.Info.Config.Subjects);
        Assert.Equal(StreamConfigStorage.File, dlq.Info.Config.Storage);
        Assert.Equal(-1, dlq.Info.Config.MaxMsgSize);
    }

    [Fact]
    public async Task Initialization_IsIdempotentAcrossRepeatedRunsAndHostRestarts()
    {
        await using (var first = await MessagingTestHost.StartAsync(nats.Url))
        {
            await first.WaitReadyAsync();
            await first.Initializer.EnsureStreamsAsync(CancellationToken.None);
            await first.Initializer.EnsureStreamsAsync(CancellationToken.None);
        }

        await using var second = await MessagingTestHost.StartAsync(nats.Url);
        await second.WaitReadyAsync();
        await second.Initializer.EnsureStreamsAsync(CancellationToken.None);

        var events = await second.JetStream.GetStreamAsync(EventNames.EventsStream);
        var dlq = await second.JetStream.GetStreamAsync(EventNames.DlqStream);
        Assert.Equal(EventNames.EventsStreamSubjects, events.Info.Config.Subjects);
        Assert.Equal(EventNames.DlqStreamSubjects, dlq.Info.Config.Subjects);
    }

    [Fact]
    public async Task Subjects_DoNotOverlapBetweenStreams()
    {
        await using var app = await MessagingTestHost.StartAsync(nats.Url);
        await app.WaitReadyAsync();
        var js = app.JetStream;

        var eventsAck = await js.PublishAsync(EventNames.ActivityImportedSubject, new byte[] { 1 });
        var dlqAck = await js.PublishAsync("dlq.test-consumer", new byte[] { 2 });

        eventsAck.EnsureSuccess();
        dlqAck.EnsureSuccess();
        Assert.Equal(EventNames.EventsStream, eventsAck.Stream);
        Assert.Equal(EventNames.DlqStream, dlqAck.Stream);
    }

    [Fact]
    public async Task DlqSubject_DoesNotEnterEventsStream()
    {
        await using var app = await MessagingTestHost.StartAsync(nats.Url);
        await app.WaitReadyAsync();
        var js = app.JetStream;
        var events = await js.GetStreamAsync(EventNames.EventsStream);
        var before = events.Info.State.Messages;

        var ack = await js.PublishAsync("dlq.x", new byte[] { 3 });
        ack.EnsureSuccess();
        await events.RefreshAsync();

        Assert.Equal(EventNames.DlqStream, ack.Stream);
        Assert.Equal(before, events.Info.State.Messages);
    }

    [Fact]
    public async Task EventsStream_AcceptsSixMegabyteMessage()
    {
        await using var app = await MessagingTestHost.StartAsync(nats.Url);
        await app.WaitReadyAsync();

        var ack = await app.JetStream.PublishAsync(EventNames.ActivityImportedSubject, new byte[SixMegabytes]);

        ack.EnsureSuccess();
        Assert.Equal(EventNames.EventsStream, ack.Stream);
    }

    [Fact]
    public async Task Startup_WithNatsStopped_DoesNotThrowAndStreamsAreCreatedOnceNatsIsBack()
    {
        // Parte da stream inesistenti: "pronto" deve significare "creati dopo il ritorno di NATS".
        await using (var seed = await MessagingTestHost.StartAsync(nats.Url))
        {
            await seed.WaitReadyAsync();
            await seed.JetStream.DeleteStreamAsync(EventNames.EventsStream);
            await seed.JetStream.DeleteStreamAsync(EventNames.DlqStream);
        }

        await nats.StopAsync();
        try
        {
            await using var app = await MessagingTestHost.StartAsync(nats.Url);
            Assert.False(app.Readiness.IsReady);

            await nats.StartAsync();
            await app.WaitReadyAsync(TimeSpan.FromSeconds(30));

            Assert.True(app.Readiness.IsReady);
            var events = await app.JetStream.GetStreamAsync(EventNames.EventsStream);
            var dlq = await app.JetStream.GetStreamAsync(EventNames.DlqStream);
            Assert.Equal(EventNames.EventsStream, events.Info.Config.Name);
            Assert.Equal(EventNames.DlqStream, dlq.Info.Config.Name);
        }
        finally
        {
            await EnsureNatsRunningAsync();
        }
    }

    [Fact]
    public async Task WhenReadyAsync_HonoursCancellationWhileNatsIsStopped()
    {
        await nats.StopAsync();
        try
        {
            await using var app = await MessagingTestHost.StartAsync(nats.Url);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Readiness.WhenReadyAsync(cts.Token));
            Assert.False(app.Readiness.IsReady);
        }
        finally
        {
            await EnsureNatsRunningAsync();
        }
    }

    [Fact]
    public async Task ComponentLogs_WhileNatsIsStopped_NeverContainAddressOrExceptionDetails()
    {
        var logs = new CapturingLoggerProvider();
        await nats.StopAsync();
        try
        {
            await using var app = await MessagingTestHost.StartAsync(nats.Url, logs);

            Assert.True(await logs.WaitForComponentEntryAsync(
                e => e.Level == LogLevel.Warning && e.Message.Contains("NATS non disponibile"), TimeSpan.FromSeconds(15)));

            var uri = new Uri(nats.Url);
            foreach (var entry in logs.ComponentEntries)
            {
                Assert.DoesNotContain(uri.Port.ToString(), entry.Message);
                Assert.DoesNotContain(uri.Host, entry.Message);
                Assert.DoesNotContain("nats://", entry.Message);
                Assert.Null(entry.Exception);
            }
        }
        finally
        {
            await EnsureNatsRunningAsync();
        }
    }

    [Fact]
    public async Task IncompatibleExistingStream_LogsServerErrorAndRetries_WithoutMisleadingConnectionWarning()
    {
        var logs = new CapturingLoggerProvider();
        await using var connection = new NatsConnection(new NatsOpts { Url = nats.Url });
        var js = new NatsJSContext(connection);
        await DeleteStreamIfExistsAsync(js, EventNames.EventsStream);
        await js.CreateStreamAsync(new StreamConfig(EventNames.EventsStream, [.. EventNames.EventsStreamSubjects])
        {
            Storage = StreamConfigStorage.Memory,
        });
        try
        {
            await using var app = await MessagingTestHost.StartAsync(nats.Url, logs);

            // 10052: "can not change storage type".
            Assert.True(await logs.WaitForComponentEntryAsync(
                e => e.Level == LogLevel.Error && e.Message.Contains("10052"), TimeSpan.FromSeconds(15)));

            var entry = logs.ComponentEntries.First(e => e.Level == LogLevel.Error && e.Message.Contains("10052"));
            Assert.Contains("rifiutata dal server", entry.Message);
            Assert.False(app.Readiness.IsReady);
            Assert.DoesNotContain(logs.ComponentEntries, e => e.Message.Contains("NATS non disponibile"));
        }
        finally
        {
            await DeleteStreamIfExistsAsync(js, EventNames.EventsStream);
        }
    }

    [Fact]
    public async Task AddNatsMessaging_UsesDefaultUrlWhenNotConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNatsMessaging(new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();
        var connection = provider.GetRequiredService<INatsConnection>();

        Assert.Equal("nats://127.0.0.1:4222", connection.Opts.Url);
    }

    [Fact]
    public async Task AddNatsMessaging_UsesConfigurationResolvedFromProvider_NotTheOneSeenAtRegistration()
    {
        var atRegistration = new ConfigurationManager();
        atRegistration["Nats:Url"] = "nats://127.0.0.1:1";
        var overridden = new ConfigurationManager();
        overridden["Nats:Url"] = nats.Url;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNatsMessaging(atRegistration);
        // Come un override di test (WebApplicationFactory): l'IConfiguration del provider cambia dopo la registrazione.
        services.AddSingleton<IConfiguration>(overridden);

        await using var provider = services.BuildServiceProvider();
        var connection = provider.GetRequiredService<INatsConnection>();
        await connection.ConnectAsync();

        Assert.Equal(nats.Url, connection.Opts.Url);
    }

    [Fact]
    public async Task AddNatsMessaging_RespectsOverrideAddedToSameConfigurationBeforeFirstResolution()
    {
        var configuration = new ConfigurationManager();
        configuration["Nats:Url"] = "nats://127.0.0.1:1";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNatsMessaging(configuration);

        configuration["Nats:Url"] = nats.Url;

        await using var provider = services.BuildServiceProvider();
        var connection = provider.GetRequiredService<INatsConnection>();
        await connection.ConnectAsync();

        Assert.Equal(nats.Url, connection.Opts.Url);
    }

    private static async Task DeleteStreamIfExistsAsync(NatsJSContext js, string name)
    {
        try
        {
            await js.DeleteStreamAsync(name);
        }
        catch (NatsJSApiException)
        {
            // Stream inesistente.
        }
    }

    /// <summary>Se un test fallisce a NATS fermo, gli altri test della collection devono trovarlo acceso (errori non ingoiati).</summary>
    private Task EnsureNatsRunningAsync() => nats.EnsureRunningAsync();
}
