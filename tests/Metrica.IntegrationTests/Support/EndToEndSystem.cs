using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Metrica.IntegrationTests.Files;
using Metrica.IntegrationTests.Hosting;
using Metrica.Messaging;
using Metrica.TestInfrastructure;

namespace Metrica.IntegrationTests.Support;

/// <summary>Variazioni di configurazione di uno scenario.</summary>
internal sealed record SystemOptions
{
    /// <summary>Impostazioni aggiuntive di activities (ad esempio <c>Consumers:ActivityImported:MaxDeliver</c>).</summary>
    public IReadOnlyDictionary<string, string> Activities { get; init; } = new Dictionary<string, string>();

    /// <summary>Impostazioni aggiuntive di ingestion.</summary>
    public IReadOnlyDictionary<string, string> Ingestion { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Lo scenario di un test: contenitori reali (TimescaleDB, NATS della fixture) e i TRE servizi reali in processo su Kestrel con
/// porta effimera, il gateway con le destinazioni YARP sovrascritte con gli indirizzi reali degli altri due.
/// </summary>
/// <remarks>
/// <para><b>Isolamento.</b> La collection xUnit non e' parallela e ogni scenario parte da uno stato noto: NATS riavviato se
/// fermo, consumatore durevole cancellato, <c>EVENTS</c> e <c>DLQ</c> svuotati, tabelle dei due schemi con <c>TRUNCATE</c>
/// (come superutente, prima di avviare gli host). L'utente di sviluppo dei servizi (<see cref="UserId"/>) e' nuovo a ogni
/// scenario e i file hanno contenuto proprio (seme = GUID), quindi nemmeno lo stato residuo di un test fallito puo' influenzare
/// gli altri. Alla fine gli host si fermano per primi, poi NATS viene ripristinato e svuotato.</para>
/// <para><b>Configurazione.</b> Ogni servizio riceve i propri appsettings REALI (vedi <see cref="ServiceConfiguration"/>), le
/// stringhe di connessione del RUOLO vero (<c>ingestion_svc</c>, <c>activities_svc</c>), l'URL del NATS della fixture,
/// <c>Dev:UserId</c> proprio e <c>Kestrel:Endpoints:Http:Url = http://127.0.0.1:0</c>: mai le porte 5080-5082 ne' lo stack di compose.</para>
/// </remarks>
internal sealed class EndToEndSystem : IAsyncDisposable
{
    private readonly List<PrivacyProbes> _probes = [];
    private readonly NatsConnection _connection;

    private EndToEndSystem(
        TimescaleFixture db, NatsFixture nats, Guid userId, LogSink logs, NatsConnection connection,
        IngestionService ingestion, ActivitiesService activities, GatewayService gateway)
    {
        DbFixture = db;
        Nats = nats;
        UserId = userId;
        Logs = logs;
        _connection = connection;
        Ingestion = ingestion;
        Activities = activities;
        Gateway = gateway;
        Db = new Database(db);
        NatsProbe = new NatsProbe(new NatsJSContext(connection));
        GatewayClient = new ServiceClient(gateway.Address);
        IngestionClient = new ServiceClient(ingestion.Address);
        ActivitiesClient = new ServiceClient(activities.Address);
        Api = new GatewayApi(GatewayClient);
    }

    public TimescaleFixture DbFixture { get; }

    public NatsFixture Nats { get; }

    /// <summary>Identita' di sviluppo dei servizi di questo scenario (<c>Dev:UserId</c>).</summary>
    public Guid UserId { get; }

    public LogSink Logs { get; }

    public IngestionService Ingestion { get; }

    public ActivitiesService Activities { get; }

    public GatewayService Gateway { get; }

    public Database Db { get; }

    public NatsProbe NatsProbe { get; }

    public GatewayApi Api { get; }

    public ServiceClient GatewayClient { get; }

    public ServiceClient IngestionClient { get; }

    public ServiceClient ActivitiesClient { get; }

    public static async Task<EndToEndSystem> StartAsync(TimescaleFixture db, NatsFixture nats, SystemOptions? options = null)
    {
        options ??= new SystemOptions();
        var userId = Guid.NewGuid();
        var logs = new LogSink();

        // 1. Stato noto di NATS e del database PRIMA di avviare gli host.
        await nats.EnsureRunningAsync();
        var connection = new NatsConnection(new NatsOpts { Url = nats.Url, MaxReconnectRetry = -1 });
        IngestionService? ingestion = null;
        ActivitiesService? activities = null;
        GatewayService? gateway = null;
        EndToEndSystem? system = null;
        try
        {
            var js = new NatsJSContext(connection);
            // Gli stream possono mancare (primo scenario, oppure un test ha fermato NATS): li crea lo stesso componente dei servizi.
            await new NatsStreamsInitializer(js, NullLogger<NatsStreamsInitializer>.Instance).EnsureStreamsAsync(CancellationToken.None);
            await new NatsProbe(js).ResetAsync();
            await new Database(db).ResetAsync();

            // 2. I tre servizi, uno alla volta (l'avvio concorrente di piu' WebApplicationFactory nello stesso processo non e' provato).
            var ingestionSettings = ServiceConfiguration.Load("ingestion", "Metrica.Ingestion");
            Apply(ingestionSettings, new Dictionary<string, string>
            {
                ["Dev:UserId"] = userId.ToString(),
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
                ["ConnectionStrings:Ingestion"] = PooledConnectionString(db.ConnectionStringFor("ingestion")),
                ["Nats:Url"] = nats.Url,
                // Il relay legge l'outbox spesso: i test non aspettano i 500 ms predefiniti a ogni evento.
                ["Outbox:PollIntervalMs"] = "100",
            });
            Apply(ingestionSettings, options.Ingestion);
            ingestion = new IngestionService(ingestionSettings, logs.ProviderFor("ingestion"));
            ingestion.Launch();

            var activitiesSettings = ServiceConfiguration.Load("activities", "Metrica.Activities");
            Apply(activitiesSettings, new Dictionary<string, string>
            {
                ["Dev:UserId"] = userId.ToString(),
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
                ["ConnectionStrings:Activities"] = PooledConnectionString(db.ConnectionStringFor("activities")),
                ["Nats:Url"] = nats.Url,
            });
            Apply(activitiesSettings, options.Activities);
            activities = new ActivitiesService(activitiesSettings, logs.ProviderFor("activities"));
            activities.Launch();

            var gatewaySettings = ServiceConfiguration.Load("gateway", "Metrica.Gateway");
            // L'override sostituisce la destinazione "primary" del file reale: se non esistesse (o ce ne fossero altre) ne verrebbe
            // aggiunta una seconda e il gateway bilancerebbe anche verso l'indirizzo di sviluppo.
            ServiceConfiguration.AssertSinglePrimaryDestination(gatewaySettings, "ingestion");
            ServiceConfiguration.AssertSinglePrimaryDestination(gatewaySettings, "activities");
            Apply(gatewaySettings, new Dictionary<string, string>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
                ["ReverseProxy:Clusters:ingestion:Destinations:primary:Address"] = ingestion.Address,
                ["ReverseProxy:Clusters:activities:Destinations:primary:Address"] = activities.Address,
            });
            gateway = new GatewayService(gatewaySettings, logs.ProviderFor("gateway"));
            gateway.Launch();

            system = new EndToEndSystem(db, nats, userId, logs, connection, ingestion, activities, gateway);
            await system.WaitUntilReadyAsync();
            return system;
        }
        catch
        {
            // Un avvio fallito non lascia nulla in vita: se il sistema e' stato costruito basta il suo DisposeAsync (client, host,
            // NATS, connessione), altrimenti si rilasciano gli host e la connessione gia' creati.
            try
            {
                if (system is not null)
                {
                    await system.DisposeAsync();
                }
                else
                {
                    await DisposeAllAsync(
                        [
                            () => gateway?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => activities?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => ingestion?.DisposeAsync() ?? ValueTask.CompletedTask,
                            () => connection.DisposeAsync(),
                        ]);
                }
            }
            catch (Exception)
            {
                // L'errore dell'avvio e' quello che conta: la pulizia e' a massimo sforzo.
            }

            throw;
        }
    }

    /// <summary>
    /// Pool di connessioni ATTIVO, come in sviluppo reale (la fixture lo disattiva per le connessioni di verifica dei test, che
    /// restano cosi). Pool piccolo: un host usa poche connessioni (EF, relay, consumatore, COPY) e a fine scenario
    /// <see cref="DisposeAsync"/> svuota i pool (<c>ClearAllPools</c>), cosi nessuna connessione idle sopravvive al reset.
    /// </summary>
    private static string PooledConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = true, MaxPoolSize = 20 }.ConnectionString;

    /// <summary>
    /// Gli host devono essere gia' connessi a NATS (alcuni scenari fermano il server): stream creati dai due servizi, consumatore
    /// durevole presente, relay dell'outbox in funzione, e le tre rotte di salute rispondono.
    /// </summary>
    private async Task WaitUntilReadyAsync()
    {
        foreach (var service in new IServiceProvider[] { Ingestion.Services, Activities.Services })
        {
            await service.GetRequiredService<INatsStreamsReadiness>().WhenReadyAsync(new CancellationTokenSource(Eventually.Normal).Token);
        }

        await Eventually.ValueAsync(
            () => NatsProbe.DurableAsync(),
            durable => durable is not null,
            Eventually.Normal,
            "il consumatore durevole activities-activity-imported esiste");

        // Il relay ha completato almeno un ciclo di lettura dell'outbox: e' connesso al database e al bus.
        var relay = Ingestion.Services.GetRequiredService<OutboxRelayStats>();
        await Eventually.AssertAsync(
            () => Task.FromResult(relay.Cycles > 0),
            Eventually.Normal,
            "il relay dell'outbox di ingestion ha completato un ciclo");

        foreach (var address in new[] { Ingestion.Address, Activities.Address, Gateway.Address })
        {
            using var client = new ServiceClient(address);
            var ready = await Eventually.ValueAsync(
                () => client.GetAsync("/health/ready"),
                r => r.Status == System.Net.HttpStatusCode.OK,
                Eventually.Normal,
                $"{address}/health/ready risponde 200");
            Assert.Equal(System.Net.HttpStatusCode.OK, ready.Status);
        }
    }

    // ---- Operazioni ricorrenti ----------------------------------------------------------------------------------------------

    /// <summary>Contatori del consumatore di activities (da quando l'host e' partito).</summary>
    public EventConsumerCounters ConsumerCounters =>
        Activities.Services.GetRequiredService<EventConsumerStats>().For(NatsProbe.ActivitiesDurable);

    public OutboxRelayStats RelayStats => Ingestion.Services.GetRequiredService<OutboxRelayStats>();

    /// <summary>Carica un file dal gateway e ne registra i dati sensibili per il controllo finale dei log.</summary>
    public Task<ApiResponse> UploadAsync(GeneratedFile file)
    {
        _probes.Add(file.Probes);
        return Api.UploadAsync(file);
    }

    public int PrivacyProbeCount => _probes.Sum(p => p.Count);

    /// <summary>Registra cio che i log non devono contenere (file o eventi costruiti dal test).</summary>
    public void Protect(PrivacyProbes probes) => _probes.Add(probes);

    /// <summary>
    /// REQ-034: nessun log dei tre servizi (messaggi, valori di stato, scope, eccezioni) contiene i dati dei file e degli eventi
    /// usati nello scenario. Verifica anche che i log non siano vuoti (altrimenti il controllo passerebbe a vuoto).
    /// </summary>
    public void AssertLogsContainNoPersonalData()
    {
        var all = new PrivacyProbes();
        foreach (var probes in _probes)
        {
            all.Merge(probes);
        }

        var entries = Logs.Entries;
        Assert.True(entries.Count > 0, "Nessun log catturato: il controllo sulla privacy passerebbe a vuoto.");
        Assert.True(all.Count > 0, "Nessun dato da cercare nei log registrato dallo scenario.");
        var findings = all.Find(entries);
        Assert.True(findings.Count == 0, $"Dati personali nei log ({findings.Count}):\n{string.Join('\n', findings.Distinct().Take(20))}");
    }

    /// <summary>Nessuna voce di livello Error o superiore nei log dei tre servizi dopo <paramref name="mark"/> (vedi <see cref="LogSink.Count"/>).</summary>
    public void AssertNoErrorsSince(int mark)
    {
        var errors = Logs.Since(mark).Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "Errori nei log (" + errors.Count + "):" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Take(10).Select(e => $"[{e.Service}] {e.Category}: {e.Text}")));
    }

    public async ValueTask DisposeAsync() =>
        await DisposeAllAsync(
            [
                () => { GatewayClient.Dispose(); return ValueTask.CompletedTask; },
                () => { IngestionClient.Dispose(); return ValueTask.CompletedTask; },
                () => { ActivitiesClient.Dispose(); return ValueTask.CompletedTask; },
                // Prima gli host (nessun consumatore vivo alla pulizia), ognuno comunque: un errore non ferma gli altri...
                () => Gateway.DisposeAsync(),
                () => Activities.DisposeAsync(),
                () => Ingestion.DisposeAsync(),
                // ...poi i pool (niente connessioni idle durante il reset), NATS e il suo stato, e in ogni caso la connessione.
                () => { NpgsqlConnection.ClearAllPools(); return ValueTask.CompletedTask; },
                async () =>
                {
                    await Nats.EnsureRunningAsync();
                    await NatsProbe.ResetAsync();
                },
                () => _connection.DisposeAsync(),
            ]);

    /// <summary>
    /// Esegue TUTTE le azioni anche se qualcuna fallisce; poi rilancia la prima eccezione (se sono piu' d'una, tutte in
    /// <see cref="AggregateException"/>).
    /// </summary>
    private static async Task DisposeAllAsync(IReadOnlyList<Func<ValueTask>> steps)
    {
        var errors = new List<Exception>();
        foreach (var step in steps)
        {
            try
            {
                await step();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        }
        else if (errors.Count > 1)
        {
            throw new AggregateException("Errori durante la pulizia dello scenario.", errors);
        }
    }

    private static void Apply(Dictionary<string, string> settings, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }
    }
}
