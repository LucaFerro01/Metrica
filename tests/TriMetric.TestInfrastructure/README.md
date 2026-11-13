# TriMetric.TestInfrastructure

Fixture xUnit condivise (Testcontainers) per i test che hanno bisogno di TimescaleDB o NATS (DEC-013).
Richiedono Docker o Podman avviato. Usano i file REALI di `deploy/` (copiati nel container), quindi
ruoli, schemi, permessi e configurazione di NATS sono quelli di sviluppo.

Riferimento dal progetto di test: `<ProjectReference Include="..\..\..\..\tests\TriMetric.TestInfrastructure\TriMetric.TestInfrastructure.csproj" />`
(percorso relativo adeguato), e `PackageReference` a `xunit`, `Npgsql` / `NATS.Net` secondo necessità (senza `Version`).

## TimescaleFixture

Immagine `docker.io/timescale/timescaledb:latest-pg16`, inizializzata con `deploy/postgres/init/01-schemas.sql`.
Un solo database condiviso: isolare i test con identificativi unici (GUID) e `TRUNCATE`, non con `CREATE DATABASE`
(si perderebbero estensione e grant). `InitializeAsync` attende (polling, 60 s) che l'inizializzazione sia finita.

- `AdminConnectionString`: superutente (setup e verifiche amministrative).
- `ConnectionStringFor(service)`: ruolo `<service>_svc`, password `dev_only_<service>`. Servizi: `identity`,
  `ingestion`, `activities`, `analytics`, `training_plan` (lo schema di training-plan è `training_plan`).
  Il `search_path` del ruolo è `<schema>, public`, quindi `create_hypertable(...)` non va qualificata.

```csharp
public sealed class MyTests(TimescaleFixture db) : IClassFixture<TimescaleFixture>
{
    [Fact]
    public async Task Works()
    {
        await using var connection = new NpgsqlConnection(db.ConnectionStringFor("activities"));
        await connection.OpenAsync();
    }
}
```

## NatsFixture

Immagine `docker.io/library/nats:2` avviata con `deploy/nats/nats-server.conf` (JetStream, `max_payload` 8 MB).
La porta host è scelta una volta sola in `InitializeAsync` e non cambia dopo un riavvio.

- `Url`: `nats://127.0.0.1:<porta>`; `MonitorUrl`: monitoraggio HTTP (`/healthz`, `/varz`).
- `StopAsync()` / `StartAsync()`: fermano e riavviano lo stesso container (stream e dati JetStream restano);
  `StartAsync` attende che il server sia pronto (`/healthz` e connessione client).
- Usare nomi di stream e subject unici per test.

```csharp
public sealed class MyNatsTests(NatsFixture nats) : IClassFixture<NatsFixture>
{
    [Fact]
    public async Task Reconnects()
    {
        await nats.StopAsync();
        await nats.StartAsync();   // stesso nats.Url
    }
}
```

## Condividere un container tra più classi

Per usare lo stesso container in più classi di test dichiarare una collection:

```csharp
[CollectionDefinition("timescale")]
public sealed class TimescaleCollection : ICollectionFixture<TimescaleFixture>;

[Collection("timescale")]
public sealed class MyTests(TimescaleFixture db) { }
```

Le classi della stessa collection non girano in parallelo; un test che ferma NATS deve stare in una collection
che non condivide il container con altri test che lo usano.
