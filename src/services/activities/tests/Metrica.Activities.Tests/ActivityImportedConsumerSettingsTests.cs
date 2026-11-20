using Microsoft.Extensions.Configuration;
using Metrica.Contracts;
using Metrica.Messaging;
using Metrica.TestInfrastructure;

namespace Metrica.Activities.Tests;

/// <summary>
/// Opzioni del consumatore <c>activities-activity-imported</c>: valori predefiniti, override da configurazione
/// (<c>Consumers:ActivityImported:*</c>), validazione all'avvio e prova che l'override arrivi DAVVERO al consumatore.
/// Questa classe non avvia container: i test sull'host sono in <see cref="ActivityImportedConsumerHostSettingsTests"/>.
/// </summary>
[Trait("Category", "Consumer")]
[Trait("Category", "Persistence")]
public sealed class ActivityImportedConsumerSettingsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => $"Consumers:ActivityImported:{v.Key}", v => v.Value))
            .Build();

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var options = ActivityImportedConsumer.CreateOptions(new ConfigurationBuilder().Build());

        Assert.Equal("activities-activity-imported", options.DurableName);
        Assert.Equal(EventNames.ActivityImportedSubject, options.FilterSubject);
        Assert.Equal("ingestion.activity.imported", options.FilterSubject);
        Assert.Equal(EventNames.EventsStream, options.Stream);
        Assert.Equal(5, options.MaxDeliver);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], options.Backoff);
        Assert.Equal(TimeSpan.FromSeconds(60), options.AckWait);
        Assert.Equal(1, options.MaxConcurrency);
        Assert.True(options.Backoff.Zip(options.Backoff.Skip(1)).All(p => p.Second > p.First), "backoff crescente");
    }

    [Fact]
    public void EmptySection_KeepsTheDefaults()
    {
        var options = ActivityImportedConsumer.CreateOptions(Config());

        Assert.Equal(5, options.MaxDeliver);
        Assert.Equal(TimeSpan.FromSeconds(60), options.AckWait);
    }

    [Fact]
    public void Overrides_AreApplied_AndTheNameAndFilterAreNotConfigurable()
    {
        var options = ActivityImportedConsumer.CreateOptions(Config(
            ("MaxDeliver", "2"),
            ("AckWaitSeconds", "7"),
            ("BackoffMilliseconds:0", "50"),
            ("BackoffMilliseconds:1", "150")));

        Assert.Equal(2, options.MaxDeliver);
        Assert.Equal(TimeSpan.FromSeconds(7), options.AckWait);
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150)], options.Backoff);
        Assert.Equal("activities-activity-imported", options.DurableName);
        Assert.Equal(EventNames.ActivityImportedSubject, options.FilterSubject);
    }

    [Fact]
    public void ABackoffOverride_ReplacesTheDefaultBackoff_InsteadOfBeingAppendedToIt()
    {
        var options = ActivityImportedConsumer.CreateOptions(Config(("BackoffMilliseconds:0", "50")));

        Assert.Equal([TimeSpan.FromMilliseconds(50)], options.Backoff);
    }

    [Theory]
    [InlineData("MaxDeliver", "0", "MaxDeliver")]
    [InlineData("MaxDeliver", "-3", "MaxDeliver")]
    [InlineData("MaxDeliver", "abc", "MaxDeliver")]
    [InlineData("MaxDeliver", "", "MaxDeliver")]
    [InlineData("MaxDeliver", "2.5", "MaxDeliver")]
    [InlineData("MaxDeliver", "101", "MaxDeliver")]
    [InlineData("AckWaitSeconds", "0", "AckWaitSeconds")]
    [InlineData("AckWaitSeconds", "-1", "AckWaitSeconds")]
    [InlineData("AckWaitSeconds", "tanto", "AckWaitSeconds")]
    [InlineData("AckWaitSeconds", "86401", "AckWaitSeconds")]
    [InlineData("BackoffMilliseconds:0", "0", "BackoffMilliseconds")]
    [InlineData("BackoffMilliseconds:0", "-5", "BackoffMilliseconds")]
    [InlineData("BackoffMilliseconds:0", "1s", "BackoffMilliseconds")]
    [InlineData("BackoffMilliseconds:0", "", "BackoffMilliseconds")]
    [InlineData("BackoffMilliseconds:0", "3600001", "BackoffMilliseconds")]
    [InlineData("BackoffMillis", "50", "BackoffMillis")]   // chiave sconosciuta (refuso): mai ignorata in silenzio
    [InlineData("DurableName", "altro", "DurableName")]    // il nome non e' configurabile
    [InlineData("MaxConcurrency", "4", "MaxConcurrency")]
    public void InvalidValues_FailAtStartup_WithAMessageNamingTheKey(string key, string value, string expectedInMessage)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ActivityImportedConsumer.CreateOptions(Config((key, value))));

        Assert.Contains("Consumers:ActivityImported", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedInMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TooManyBackoffSteps_Fail()
    {
        var steps = Enumerable.Range(0, 11).Select(i => ($"BackoffMilliseconds:{i}", (string?)"100")).ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => ActivityImportedConsumer.CreateOptions(Config(steps)));

        Assert.Contains("BackoffMilliseconds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatedOptions_AreAcceptedByTheMessagingValidation()
    {
        // Gli estremi consentiti dal servizio devono essere accettati anche dalla validazione di Messaging (che gira in AddEventConsumer).
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var options = ActivityImportedConsumer.CreateOptions(Config(
            ("MaxDeliver", "100"), ("AckWaitSeconds", "86400"), ("BackoffMilliseconds:0", "1"), ("BackoffMilliseconds:1", "3600000")));

        services.AddEventConsumer<ActivitiesDbContext, ActivityImported, ActivityImportedHandler>(options);
    }
}

/// <summary>
/// L'override delle opzioni arriva davvero al consumatore dell'host reale. ATTENZIONE: <c>AddEventConsumer</c> riceve opzioni
/// costruite alla REGISTRAZIONE (non pigre), cioe' leggendo <c>builder.Configuration</c> alla costruzione dell'host: gli
/// override di <c>ConfigureAppConfiguration</c> arrivano dopo e verrebbero ignorati in silenzio, serve <c>UseSetting</c>
/// (come per <c>Dev:UserId</c>). Per questo i test verificano gli effetti (configurazione sul server NATS, tempi, tentativi)
/// e non solo che l'host si avvii.
/// </summary>
[Collection(InfrastructureCollection.Name)]
[Trait("Category", "Consumer")]
[Trait("Category", "Persistence")]
public sealed class ActivityImportedConsumerHostSettingsTests(NatsFixture nats, TimescaleFixture db)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WithoutOverrides_TheServerSideConsumerHasTheDocumentedConfiguration()
    {
        await using var s = await ImportScenario.CreateAsync(nats, db);
        await using var host = s.StartHost();

        ConsumerInfoSnapshot? info = null;
        await Eventually.AssertAsync(
            async () => (info = ConsumerInfoSnapshot.From(await s.ConsumerInfoAsync())) is not null,
            Timeout,
            "durevole creato dal consumatore");

        Assert.Equal("activities-activity-imported", info!.Name);
        Assert.Equal("ingestion.activity.imported", info.FilterSubject);
        Assert.Equal(TimeSpan.FromSeconds(60), info.AckWait);
        Assert.Equal(1, info.MaxAckPending);
        Assert.Equal(-1, info.MaxDeliver);   // il limite dei tentativi lo applica il consumatore (vedi Messaging)
    }

    [Fact]
    public async Task UseSettingOverrides_AreReallyApplied_AckWaitOnTheServer_MaxDeliverAndBackoffInBehaviour()
    {
        await using var s = await ImportScenario.CreateAsync(nats, db);
        await using var host = s.StartHost(new Dictionary<string, string?>
        {
            ["Consumers:ActivityImported:MaxDeliver"] = "2",
            ["Consumers:ActivityImported:AckWaitSeconds"] = "7",
            ["Consumers:ActivityImported:BackoffMilliseconds:0"] = "3000",
        });
        var counters = ImportScenario.CountersOf(host);

        ConsumerInfoSnapshot? info = null;
        await Eventually.AssertAsync(
            async () => (info = ConsumerInfoSnapshot.From(await s.ConsumerInfoAsync())) is not null,
            Timeout,
            "durevole creato dal consumatore");
        Assert.Equal(TimeSpan.FromSeconds(7), info!.AckWait);   // predefinito 60 s: se l'override fosse ignorato resterebbe 60

        // MaxDeliver 2 e Backoff 3 s: un guasto permanente produce DUE tentativi a distanza di ALMENO 3 s e poi la DLQ.
        // Con i valori predefiniti (5 tentativi, prima attesa 1 s) sarebbero 5 tentativi e circa 1 s tra i primi due. Si verifica
        // solo un limite INFERIORE (il ritardo non puo' essere piu' breve di quello chiesto): nessuna soglia superiore, quindi
        // nessun falso rosso sotto carico.
        s.Probe.FailCommits(DatabaseProbe.Always);
        await s.PublishAsync(ImportedEvents.Valid(points: 5, laps: 1));
        await Eventually.AssertAsync(async () => await s.DlqCountAsync() == 1, TimeSpan.FromSeconds(60), "evento in DLQ");

        var dlq = Assert.Single(await s.DlqAsync());
        Assert.Equal("2", dlq.Headers["Dlq-Deliveries"]);
        var failures = s.Probe.Failures.ToArray();
        Assert.Equal(2, failures.Length);
        var gap = failures[1] - failures[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(2500), $"Attesa tra i tentativi {gap.TotalMilliseconds:0} ms: il backoff configurato di 3000 ms non e' stato applicato (predefinito: 1000 ms).");
        Assert.Equal(1, counters.Retried);
    }

    [Fact]
    public async Task ConfigureAppConfigurationOverrides_AreIgnoredByDesign_SoTestsMustUseUseSetting()
    {
        // Documenta la trappola: MaxDeliver via 'extra' (ConfigureAppConfiguration) NON arriva al consumatore, che usa il predefinito (5).
        await using var s = await ImportScenario.CreateAsync(nats, db);
        await using var host = ActivitiesHost.Development(
            db,
            nats,
            extra: new Dictionary<string, string?> { ["Consumers:ActivityImported:AckWaitSeconds"] = "7" });
        _ = host.Services;

        ConsumerInfoSnapshot? info = null;
        await Eventually.AssertAsync(
            async () => (info = ConsumerInfoSnapshot.From(await s.ConsumerInfoAsync())) is not null,
            Timeout,
            "durevole creato dal consumatore");

        Assert.Equal(TimeSpan.FromSeconds(60), info!.AckWait);
    }

    [Theory]
    [InlineData("Consumers:ActivityImported:MaxDeliver", "0", "MaxDeliver")]
    [InlineData("Consumers:ActivityImported:AckWaitSeconds", "abc", "AckWaitSeconds")]
    [InlineData("Consumers:ActivityImported:BackoffMilliseconds:0", "-1", "BackoffMilliseconds")]
    [InlineData("Consumers:ActivityImported:Backoff", "50", "Backoff")]
    public async Task InvalidSettings_MakeTheHostFailAtStartup_WithAClearMessage(string key, string value, string expected)
    {
        await using var host = ActivitiesHost.Development(db, nats, startupSettings: new Dictionary<string, string?> { [key] = value });

        var error = Assert.ThrowsAny<Exception>(() => host.Start());

        var messages = new List<string>();
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        Assert.Contains(messages, m => m.Contains("Consumers:ActivityImported", StringComparison.Ordinal) && m.Contains(expected, StringComparison.Ordinal));
    }

    private sealed record ConsumerInfoSnapshot(string Name, string? FilterSubject, TimeSpan AckWait, long MaxAckPending, long MaxDeliver)
    {
        public static ConsumerInfoSnapshot? From(NATS.Client.JetStream.Models.ConsumerInfo? info) => info is null
            ? null
            : new ConsumerInfoSnapshot(
                info.Name,
                info.Config.FilterSubject,
                info.Config.AckWait,
                info.Config.MaxAckPending,
                info.Config.MaxDeliver);
    }
}
