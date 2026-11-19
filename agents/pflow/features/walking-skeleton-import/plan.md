---
schema_version: 1
status: ready
tracking_ref: null
updated_at: 2026-11-19
---

# Implementation Plan: Scheletro del sistema e import di file GPX/FIT

## Goal

Un file GPX o FIT caricato dal web viene tradotto da `ingestion`, pubblicato come `ActivityImported` su NATS JetStream con outbox, salvato in `activities` (con stream in hypertable) in modo idempotente, e mostrato in elenco e dettaglio nel web passando dal `gateway` YARP.

## Global constraints

- Decisioni vincolanti in `decisions.md` (DEC-001…DEC-016): ogni task cita quelle che applica.
- Target `net10.0`, SDK fissato da `global.json` (DEC-004). Un progetto per servizio.
- Nessun codice condiviso di produzione oltre a `contracts`, `ServiceDefaults`, `Messaging`; nessuna dipendenza di questi da un servizio; un servizio non legge lo schema di un altro (REQ-023). `tests/TriMetric.TestInfrastructure` è codice di test condiviso (DEC-013).
- Ogni endpoint dei servizi usa `RequireAuthorization()` e legge `userId` dal `ClaimsPrincipal`, mai dalla richiesta (DEC-006). Ogni servizio espone `public partial class Program` per i test con `WebApplicationFactory`.
- Istanti in UTC, unità SI (metri, secondi, watt, m/s). `Sport` serializzato come stringa minuscola ovunque (DEC-005).
- Nei log mai coordinate, frequenza cardiaca o contenuto dei file (REQ-034).
- Nomi di tabelle e colonne in `snake_case` tramite `EFCore.NamingConventions` (DEC-003); ogni `DbContext` ha una `IDesignTimeDbContextFactory`.
- `migrations: approval`: ogni migrazione si crea solo dopo un sì esplicito e mai si applica a mano a un database (`dotnet ef database update` vietato); controllo di ambito obbligatorio contro `Schema changes`.
- `commit_policy: manual`: ogni task termina con una proposta di commit; il commit lo fa l'utente. Commit conventional senza `#ref` (DEC-011).
- Comandi di verifica scritti per **Windows PowerShell** (su PowerShell `curl` è un alias: usare `curl.exe` o `Invoke-RestMethod`).
- Azioni umane previste: avviare Docker; ricreare il volume del database dopo la modifica dello script di inizializzazione (`docker compose -f deploy/compose.yaml down -v`, T04); attivare `pnpm` prima di T16 (`corepack enable`, poi `corepack prepare pnpm@latest --activate`); approvare le due migrazioni (T09, T11).
- Dipendenze ammesse (versioni verificate su NuGet/npm il 2026-11-12, tutte in `Directory.Packages.props`): `NATS.Net` 3.3.x, `Garmin.FIT.Sdk` 21.221.0, `Npgsql` 10.0.x, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Microsoft.EntityFrameworkCore.*` 10.0.x (con `.Design` in `PrivateAssets=all`), `EFCore.NamingConventions` 10.0.1, `Yarp.ReverseProxy` 2.3.0, `Microsoft.AspNetCore.OpenApi` 10.0.x, OpenTelemetry 1.x, `xunit` 2.9.3 con `xunit.extensibility.core` 2.9.3 (per `IAsyncLifetime` nelle librerie di test), `xunit.runner.visualstudio` 3.1.5 e `Microsoft.NET.Test.Sdk` 18.12.0, `Testcontainers.PostgreSql` 4.16.0 (la fixture NATS usa `ContainerBuilder` generico: `Testcontainers.Nats` resta disponibile ma non è usato), `Microsoft.AspNetCore.Mvc.Testing` 10.0.x. Frontend: versioni correnti di React 19, Vite, TanStack Router/Query, `openapi-typescript` 7, `openapi-fetch`, Vitest, Testing Library, Tailwind CSS, Playwright; `packageManager` di pnpm fissato in `package.json`.

## Requirements coverage

| Requirement | Acceptance | Tasks | Verification |
|---|---|---|---|
| REQ-001, REQ-002 | AC-013 | T01 | `dotnet build TriMetric.slnx -warnaserror` |
| REQ-003 | AC-009 | T03 | `dotnet test src/shared/ServiceDefaults/tests/TriMetric.ServiceDefaults.Tests` (health, `ready`, `correlationId` nei log, OTel registrato) |
| REQ-004 | — | T02 | `dotnet test src/shared/contracts/tests/TriMetric.Contracts.Tests` |
| REQ-005 | AC-013 | T19 | prova di `scripts/dev.ps1` su clone pulito (T20) |
| REQ-006, REQ-009 | AC-001, AC-004 | T10 | `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests` |
| REQ-007, REQ-008, REQ-031 | AC-001, AC-002 | T08, T10 | test dei parser con file di esempio e test di dedup |
| REQ-010 | AC-004 | T08, T10 | parser che onorano il token; test di timeout con parser lento |
| REQ-011 | AC-003 | T10, T15 | test di dedup (anche concorrente) e in integrazione |
| REQ-012 | AC-005 | T05, T09, T10 | test di outbox transazionale |
| REQ-013 | AC-005 | T06, T10, T15 | test con NATS fermato e riavviato |
| REQ-014, REQ-015 | AC-006 | T07, T12, T15 | test consumatore idempotente |
| REQ-016 | AC-014 | T11, T12 | test: tabella risulta hypertable, `COPY` di 50.000 punti |
| REQ-017 | AC-012 | T07, T15 | test di retry e DLQ |
| REQ-018 | AC-010, AC-011 | T13 | `dotnet test src/services/activities/tests/TriMetric.Activities.Tests` |
| REQ-019 | — | T13 | test che `/openapi/v1.json` espone `ActivityListItem`, `ActivityDetail`, `StreamColumns` |
| REQ-020 | AC-001 | T14 (+ test di bind in T09, T11) | test di instradamento e test sul solo bind su `127.0.0.1` |
| REQ-021 | AC-009 | T03, T10, T13 | test in ambiente non-Development (401) |
| REQ-022 | AC-007 | T10, T13, T15 | test di isolamento tra utenti |
| REQ-023 | AC-008 | T04, T09, T11, T15 | test dei permessi tra schemi |
| REQ-024 | AC-013 | T09, T11 | migrazioni applicate all'avvio nei test di integrazione |
| REQ-025, REQ-029 | AC-013 | T16 | `pnpm -C src/web build`; `gen:api` senza differenze |
| REQ-026 | AC-001, AC-004 | T17 | `pnpm -C src/web test` |
| REQ-027, REQ-028 | AC-001, AC-010 | T18 | `pnpm -C src/web test` |
| REQ-030, REQ-037 | AC-001 | T16, T17, T18 | controllo automatico `scrollWidth <= innerWidth` a 375 px e a 1280 px (Playwright); navigazione in basso sotto 768 px e barra laterale da 768 px |
| REQ-039 | — | T16 | test del tema (sistema/chiaro/scuro, persistenza) e della parità delle chiavi di traduzione |
| REQ-038 | — | T16, T17 | test della base URL configurabile; regola di lint su `localStorage` e `window.open`; metadati di viewport |
| REQ-032 | AC-001…AC-008, AC-012, AC-014 | T15 | `dotnet test tests/TriMetric.IntegrationTests` |
| REQ-033 | AC-013 | T19 | revisione del documento e prova da zero in T20 |
| REQ-034 | — | T03, T15 | test sui log con provider in memoria |
| REQ-035 | AC-014 | T08, T10, T15 | test con 100.000 e 100.001 punti |
| REQ-036 | AC-014 | T02, T04, T08, T15 | test del caso peggiore < 8 MB; `max_payload` verificato dal server; 100.000 punti in integrazione |

## Technical decisions

Recorded in `decisions.md`.

## Implementation structure

### Files

- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `TriMetric.slnx`, `.editorconfig`, `.config/dotnet-tools.json` (T01)
- Create: `src/shared/contracts/TriMetric.Contracts/**` e `.../tests/TriMetric.Contracts.Tests/**` (T02)
- Create: `src/shared/ServiceDefaults/TriMetric.ServiceDefaults/**` e `.../tests/TriMetric.ServiceDefaults.Tests/**` (T03)
- Create: `deploy/nats/nats-server.conf`, `tests/TriMetric.TestInfrastructure/**`, `tests/TriMetric.TestInfrastructure.Tests/**`; Modify: `deploy/compose.yaml`, `deploy/postgres/init/01-schemas.sql` (T04)
- Create: `src/shared/Messaging/TriMetric.Messaging/**` e `.../tests/TriMetric.Messaging.Tests/**` (T05, T06, T07)
- Create: `src/services/ingestion/src/TriMetric.Ingestion/**` e `.../tests/TriMetric.Ingestion.Tests/**` (T08, T09, T10)
- Create: `src/services/activities/src/TriMetric.Activities/**` e `.../tests/TriMetric.Activities.Tests/**` (T11, T12, T13)
- Create: `src/services/gateway/src/TriMetric.Gateway/**` e `.../tests/TriMetric.Gateway.Tests/**` (T14)
- Create: `tests/TriMetric.IntegrationTests/**` (T15)
- Create: `src/web/**` (T16–T18)
- Create: `src/web/capacitor.config.ts`, `src/web/android/`, e le origini ammesse del gateway (T21, dopo lo scheletro)
- Create: `scripts/dev.ps1`, `scripts/dev.sh`, `docs/getting-started.md`; Modify: `README.md` (T19)
- Modify: `ARCHITECTURE/**`, `src/services/*/README.md` (T20)

### Interfaces

Produced (nomi e firme di riferimento; se l'implementazione li cambia, il piano si aggiorna prima di proseguire):

```csharp
// TriMetric.Contracts
public sealed record EventEnvelope<T>(Guid Id, string Type, int Version, DateTimeOffset OccurredAt,
    Guid UserId, Guid CorrelationId, T Data);
public enum Sport { Run, Ride, Swim, Other }   // JSON/DB/API: "run" | "ride" | "swim" | "other"
public sealed record ActivityImported(string Source, string ExternalId, Sport Sport, DateTimeOffset StartTime,
    double ElapsedSeconds, double MovingSeconds, double DistanceMeters, double? ElevationGainMeters,
    ActivitySummary Summary, IReadOnlyList<LapData> Laps, StreamColumns Stream);
public sealed record ActivitySummary(double? AvgHeartRate, double? MaxHeartRate, double? AvgPowerWatts, double? AvgCadence);
public sealed record LapData(DateTimeOffset StartTime, double ElapsedSeconds, double DistanceMeters);
// Colonne parallele, tutte lunghe quanto TimeOffsetSeconds; una colonna del tutto assente è null.
// Valori già arrotondati da ingestion (DEC-012): tempo 0,001 s; lat/lon 7 decimali; quota 0,1 m; velocità 0,001 m/s; FC, potenza, cadenza interi.
public sealed record StreamColumns(IReadOnlyList<double> TimeOffsetSeconds, IReadOnlyList<double?>? Latitude,
    IReadOnlyList<double?>? Longitude, IReadOnlyList<double?>? AltitudeMeters, IReadOnlyList<double?>? HeartRate,
    IReadOnlyList<double?>? PowerWatts, IReadOnlyList<double?>? Cadence, IReadOnlyList<double?>? SpeedMetersPerSecond);
public static class EventNames { const string ActivityImported = "ActivityImported";
    const string ActivityImportedSubject = "ingestion.activity.imported"; const string EventsStream = "EVENTS";
    const string DlqStream = "DLQ"; /* EVENTS: identity.>, ingestion.>, activities.>, analytics.>, training-plan.> ; DLQ: dlq.> */ }
public static class StreamLimits { const int MaxPoints = 100_000; }

// TriMetric.ServiceDefaults
IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder b);   // OTel, health, logging con correlationId
WebApplication MapServiceDefaults(this WebApplication app);                   // /health/live, /health/ready
IHostApplicationBuilder AddDevelopmentIdentity(this IHostApplicationBuilder b); // Development: utente fisso `Dev:UserId`; altrimenti schema Deny (401). (T03: firma su IHostApplicationBuilder perché serve l'ambiente)
Guid GetUserId(this ClaimsPrincipal p);                                       // claim "sub": uno solo, UUID formato "D", non vuoto; altrimenti InvalidOperationException
// Il middleware del correlationId è registrato da AddServiceDefaults (IStartupFilter); MapServiceDefaults mappa solo gli health.

// TriMetric.Messaging
public interface IOutbox { void Add<T>(string subject, EventEnvelope<T> envelope); } // nel change tracker del DbContext del chiamante
public interface IOutboxDbContext { DbSet<OutboxMessage> Outbox { get; } }
ModelBuilder ApplyOutbox(this ModelBuilder b);                                // tabella `outbox` nello schema del servizio
IServiceCollection AddNatsMessaging(this IServiceCollection s, IConfiguration c);       // "Nats:Url"; crea EVENTS e DLQ
IServiceCollection AddOutbox<TDbContext>(this IServiceCollection s) where TDbContext : DbContext, IOutboxDbContext;       // (T05) registra IOutbox scoped; un solo contesto per servizio
IServiceCollection AddOutboxRelay<TDbContext>(this IServiceCollection s) where TDbContext : DbContext, IOutboxDbContext; // "Outbox:PollIntervalMs" (predefinito 500)
public interface INatsStreamsReadiness { bool IsReady { get; } Task WhenReadyAsync(CancellationToken ct); }   // (T05) il relay aspetta gli stream
public interface IEventHandler<T> { Task HandleAsync(EventEnvelope<T> envelope, CancellationToken ct); }
IServiceCollection AddEventConsumer<TDbContext, TData, THandler>(this IServiceCollection s, ConsumerOptions o)
    where TDbContext : DbContext, IInboxDbContext where THandler : class, IEventHandler<TData>;
ModelBuilder ApplyInbox(this ModelBuilder b);                                 // tabella `inbox`

// TriMetric.Ingestion
public interface IActivityFileParser { bool CanParse(string fileName); ParsedActivity Parse(Stream content, CancellationToken ct); }
// POST /uploads (dietro al gateway: POST /api/ingestion/uploads), multipart campo "file"
//   202 { "uploadId": Guid, "externalId": string, "status": "accepted" } | 200 { "uploadId", "externalId", "status": "duplicate" }
//   errori ProblemDetails: 400 file vuoto/malformato, 413 troppo grande, 415 tipo non supportato, 422 contenuto non valido / troppi punti / timeout di parsing

// TriMetric.Activities (dietro al gateway: GET /api/activities, GET /api/activities/{id})
// GET /activities?sport=&page=1&pageSize=20 → { items: ActivityListItem[], page, pageSize, total }
// ActivityListItem { id, externalId, sport, startTime, distanceMeters, elapsedSeconds, elevationGainMeters }
// GET /activities/{id}?maxPoints=1000 → ActivityDetail { ...ActivityListItem, movingSeconds, summary, laps, stream: StreamColumns }
```

## Tasks

### T01: Fondamenta della solution .NET

**Status:** completed
**Covers:** REQ-001, REQ-002, AC-013
**Depends on:** none
**Tier:** light — file di configurazione con specifica esatta e verifica automatica.

**Action:**
- Creare `global.json` (SDK `11.0.100`, `rollForward: latestFeature`), `Directory.Build.props` (`TargetFramework net10.0`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`), `Directory.Packages.props` con `ManagePackageVersionsCentrally true` e le versioni elencate nei vincoli, `.editorconfig`, `TriMetric.slnx` vuota.
- Creare `.config/dotnet-tools.json` con `dotnet-ef` alla versione `10.0.15`.

**Verification:**
- Run: `dotnet --version; dotnet tool restore; dotnet ef --version; dotnet build TriMetric.slnx`
- Expected: SDK 11.0.100; `dotnet ef` 10.0.x; build riuscita senza warning.

**Done when:**
- I comandi riescono; nessun pacchetto è referenziato senza versione centralizzata.

**Suggested commit:**
`chore(repo): aggiunge global.json, build condivisa e solution .NET`

### T02: Progetto `contracts` con busta ed evento

**Status:** completed
**Covers:** REQ-004, REQ-036 (formato), DEC-005, DEC-012
**Depends on:** T01
**Tier:** strong — definisce il contratto tra servizi (API pubblica interna).

**Action:**
- Creare `TriMetric.Contracts` con i tipi della sezione Interfacce (record immutabili, solo costanti, nessuna logica) e `ContractJson.Options` (camelCase, enum come stringhe minuscole, `null` esplicito per le colonne assenti).
- `TriMetric.Contracts.Tests` (xUnit): round-trip di `EventEnvelope<ActivityImported>`; camelCase e `sport` minuscolo (`"run"`); colonne `null`; **test del caso peggiore**: 100.000 punti con **tutte e 8 le colonne** valorizzate con i valori più lunghi possibili dopo l'arrotondamento di DEC-012 (tempo `99999.999`, lat/lon `-123.1234567`, quota `-1234.5`, velocità `12.345`, FC `255`, potenza `2000`, cadenza `255`) → JSON < 8 MB (8.388.608 byte).
- Aggiungere i progetti a `TriMetric.slnx`.

**Test cycle:**
1. Scrivere i test (serializzazione e caso peggiore).
2. Run `dotnet test src/shared/contracts/tests/TriMetric.Contracts.Tests`; atteso: fallimento (tipi assenti).
3. Implementare i record e `ContractJson`.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/shared/contracts/tests/TriMetric.Contracts.Tests`
- Expected: tutti verdi; il test del caso peggiore stampa la dimensione misurata (< 8 MB).

**Done when:**
- Nessuna dipendenza oltre al framework.

**Suggested commit:**
`feat(contracts): aggiunge busta eventi e ActivityImported con stream a colonne`

### T03: `ServiceDefaults` con health, OpenTelemetry e identità di sviluppo

**Status:** completed
**Covers:** REQ-003, REQ-021, REQ-034 (log), AC-009; DEC-006
**Depends on:** T01
**Tier:** strong — confine di sicurezza: l'identità fissa non deve mai attivarsi fuori da Development.

**Action:**
- `AddServiceDefaults`: OpenTelemetry (tracce e metriche, esportatore OTLP configurabile, spento senza endpoint); logging strutturato JSON con `correlationId` (scope per richiesta; header `X-Correlation-Id` accettato o generato e restituito); health check `live` (sempre sano) e `ready` (esegue i controlli registrati, per esempio database).
- `MapServiceDefaults`: `/health/live` e `/health/ready`.
- `AddDevelopmentIdentity`: in `Development` registra lo schema `DevelopmentIdentity` che autentica ogni richiesta con claim `sub` = `Dev:UserId` (UUID valido, altrimenti l'avvio fallisce); fuori da `Development` registra uno schema predefinito `Deny` che risponde **sempre 401** (mai un servizio senza schema).
- `GetUserId(ClaimsPrincipal)`: legge `sub`, lancia se assente o non UUID.
- Nessun log di corpo, intestazioni o query string di richiesta; `EnableSensitiveDataLogging` di EF Core vietato in ogni configurazione (REQ-034), con un test/analisi che lo controlla nei progetti che usano EF.
- Test (`WebApplicationFactory` su un'app minima di prova):
  - `Development`: endpoint protetto → `userId` = `Dev:UserId`; un `userId` in query o corpo non cambia l'identità;
  - `Production`: stessa richiesta → 401 (non 500) e schema `DevelopmentIdentity` non registrato; `Dev:UserId` mancante in Development → l'avvio fallisce;
  - `/health/live` 200; `/health/ready` 200 con controlli sani e 503 con un controllo non sano;
  - il `correlationId` ricevuto compare in un log emesso nella richiesta (provider di log in memoria) e nella risposta;
  - OpenTelemetry registrato (provider di tracce presente).

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/shared/ServiceDefaults/tests/TriMetric.ServiceDefaults.Tests`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/shared/ServiceDefaults/tests/TriMetric.ServiceDefaults.Tests`
- Expected: tutti verdi, incluso il 401 fuori da Development.

**Done when:**
- La guardia sull'ambiente è coperta da un test che fallisce se rimossa; nessun riferimento a un servizio.

**Suggested commit:**
`feat(servicedefaults): aggiunge health, OpenTelemetry e identità di sviluppo`

### T04: Infrastruttura di test e correzione di deploy (NATS e permessi del database)

**Status:** completed
**Covers:** REQ-023, REQ-036 (server 8 MB), AC-008; DEC-005, DEC-012, DEC-013, DEC-014
**Depends on:** T01
**Tier:** strong — permessi del database e contratto operativo di NATS.

**Action:**
- Creare `deploy/nats/nats-server.conf` (JetStream con `store_dir /data`, `http: 8222`, `max_payload: 8MB`); modificare `deploy/compose.yaml` perché `nats` parta con `-c /etc/nats/nats-server.conf` montando il file in sola lettura (commento sul suffisso `:Z` per Podman/SELinux).
- Modificare `deploy/postgres/init/01-schemas.sql` (DEC-014): per ogni ruolo `*_svc` aggiungere `GRANT USAGE ON SCHEMA public` e impostare `search_path = <schema>, public`; mantenere `REVOKE ALL ON SCHEMA public FROM PUBLIC` (nessun `CREATE` su `public`).
- Creare `tests/TriMetric.TestInfrastructure` (libreria) con `TimescaleFixture` (immagine `docker.io/timescale/timescaledb:latest-pg16`, script `deploy/postgres/init/01-schemas.sql` montato in `docker-entrypoint-initdb.d`, connessione per ogni ruolo `*_svc`; **un solo database** condiviso, isolamento tra test per identificativi e `TRUNCATE`, non con `CREATE DATABASE` che perderebbe estensione e grant) e `NatsFixture` (immagine `docker.io/library/nats:2`, `deploy/nats/nats-server.conf` montato, **porta host fissa** scelta una volta e ripetuta dopo un riavvio, helper `StopAsync/StartAsync`). Aggiungere `tests/TriMetric.TestInfrastructure.Tests` con test di fumo.
- Test di fumo: come `activities_svc`, `CREATE TABLE` in `activities` e `create_hypertable` riescono, e l'inserimento funziona; `activities_svc` non legge né scrive nello schema `ingestion` (e viceversa) e non può creare oggetti in `public`; NATS espone `max_payload` = 8 MB (informazioni di connessione) e dopo `StopAsync/StartAsync` la porta è la stessa; un messaggio da circa 6 MB è accettato e uno da oltre 8 MB rifiutato.
- **Azione umana:** dopo la modifica dello script servono `docker compose -f deploy/compose.yaml down -v` e un nuovo `up -d` (cancella i dati di sviluppo) perché il volume locale applichi lo script.

**Test cycle:**
1. Scrivere i test di fumo.
2. Run `dotnet test tests/TriMetric.TestInfrastructure.Tests`; atteso: fallimento (permessi e `max_payload` non ancora configurati).
3. Implementare configurazione e fixture.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test tests/TriMetric.TestInfrastructure.Tests`; dopo l'azione umana: `(Invoke-RestMethod http://localhost:8222/varz).max_payload`
- Expected: test verdi; `max_payload` = 8388608.

**Done when:**
- Ruoli e schemi di test sono quelli reali dello script; le fixture sono riutilizzabili da tutti i test successivi.

**Suggested commit:**
`feat(deploy): configura NATS a 8 MB, permessi sulle hypertable e fixture di test`

### T05: `Messaging` — client NATS, stream e outbox

**Status:** completed
**Covers:** REQ-012, REQ-036; DEC-001, DEC-005, DEC-012
**Depends on:** T02, T04
**Tier:** strong — consistenza transazionale e contratto operativo di NATS.

**Action:**
- I subject degli stream si leggono da `EventNames.EventsStreamSubjects` e `EventNames.DlqStreamSubjects` (T02, oggi `static readonly string[]` mutabili): copiarli prima di passarli a NATS.Net e valutare se renderli `IReadOnlyList<string>` secondo ciò che accetta `StreamConfig.Subjects`.
- `TriMetric.Messaging`: `AddNatsMessaging` (connessione con `Nats:Url`, riconnessione automatica, creazione idempotente degli stream `EVENTS` con i prefissi espliciti `identity.>`, `ingestion.>`, `activities.>`, `analytics.>`, `training-plan.>` e `DLQ` con `dlq.>`, persistenza su file, finestra di deduplicazione di 1 ora).
- Outbox: entità `OutboxMessage` (`Id`, `Subject`, `Type`, `Payload` bytea, `CreatedAt`, `PublishedAt?`, `Attempts`, `LastError?`), `ApplyOutbox` (tabella `outbox` con indice parziale sui non pubblicati), `IOutbox.Add` (nel change tracker; scrittura con il `SaveChanges` del chiamante, quindi nella stessa transazione).
- Test (con le fixture di T04): creazione degli stream riuscita, **ripetibile** (idempotente) e senza sovrapposizione di subject (un messaggio su `dlq.x` non finisce in `EVENTS`); `IOutbox.Add` + `SaveChanges` salva evento e dati di dominio in una sola transazione (un fallimento a metà non lascia una riga di outbox orfana).

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests --filter Category=Setup`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests --filter "Category=Setup|Category=Outbox"`
- Expected: verdi.

**Done when:**
- `Messaging` non dipende da alcun servizio.

**Suggested commit:**
`feat(messaging): aggiunge client NATS, stream EVENTS/DLQ e outbox`

### T06: `Messaging` — relay dell'outbox

**Status:** completed
**Covers:** REQ-012, REQ-013; AC-005; DEC-001
**Depends on:** T05
**Tier:** strong — concorrenza e consegna senza perdite né doppioni.

**Action:**
- Fixture: i test di Messaging di T05 usano `IClassFixture<TimescaleFixture>` per la classe `OutboxTests` (un contenitore per classe); da T06/T07 le nuove classi di test **condividono una collection fixture** per Timescale e per NATS (esiste già `NatsCollection`), così non si avviano contenitori in più; eventualmente sposta anche `OutboxTests` nella collection.
- `OutboxRelay<TDbContext>` (`BackgroundService`): ogni `Outbox:PollIntervalMs` (500 ms predefiniti) legge i messaggi non pubblicati in ordine di creazione con `FOR UPDATE SKIP LOCKED`, pubblica su JetStream con `Nats-Msg-Id` = id dell'evento, attende l'ack, marca `PublishedAt`; in caso di errore incrementa `Attempts`, registra `LastError` e riprova con attesa crescente. Un crash tra pubblicazione e marcatura non produce doppioni entro la finestra di deduplicazione.
- Test (fixture T04): pubblicazione a regime con latenza < 2 s; NATS fermato durante `Add`+`SaveChanges`, poi riavviato → il messaggio arriva **una sola volta**; due istanze del relay non duplicano; un messaggio da circa 6 MB è pubblicato.

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests --filter Category=Relay`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests --filter Category=Relay`
- Expected: verdi.

**Done when:**
- Il relay è sicuro con più istanze e non perde messaggi con il bus fermo.

**Suggested commit:**
`feat(messaging): aggiunge il relay dell'outbox verso JetStream`

### T07: `Messaging` — consumatore idempotente con retry e DLQ

**Status:** completed
**Covers:** REQ-014, REQ-015, REQ-017; AC-006, AC-012; DEC-001, DEC-005
**Depends on:** T05
**Tier:** strong — idempotenza, concorrenza e gestione dei fallimenti.

**Action:**
- `InboxMessage` (`MessageId`, `Consumer`, `ProcessedAt`; **PK composta (`Consumer`, `MessageId`)**), `ApplyInbox` (tabella `inbox`, colonne esplicite `message_id`, `consumer`, `processed_at`), `IInboxDbContext { DbSet<InboxMessage> Inbox }`; `EventValidationException` pubblica: un gestore la lancia per mandare un evento non valido direttamente in DLQ senza ritentare (la usa T12).
- Decisione registrata in T07: `MaxAckPending = MaxConcurrency` è TENUTO. Un messaggio che fallisce di continuo occupa lo slot per la durata del backoff (circa 18 s più la DLQ con il backoff predefinito): "non bloccano gli altri" significa "con ritardo limitato". L'alternativa (MaxAckPending più alto con buffering lato client) rischia riconsegne spurie e falsi poison pill. I test di integrazione (T12, T15) usano un `Backoff` breve.
- `AddEventConsumer<TDbContext,TData,THandler>`: consumatore durevole (nome, filtro di subject, `MaxDeliver`, backoff, `AckWait` da `ConsumerOptions`). Per ogni messaggio: deserializza la busta; il gestore è risolto **dallo stesso scope e `DbContext`** dell'inbox; in **una sola transazione** inserisce l'id nell'inbox (vincolo di unicità) ed esegue il gestore; un duplicato (id già presente) è confermato senza eseguire il gestore; `Ack` solo dopo il commit. Durante l'elaborazione lunga invia `AckProgress` per non scadere `AckWait`.
- Una violazione di unicità sui dati di dominio che il gestore tratta come "già presente" è un no-op, non un errore da ritentare (il gestore usa `ON CONFLICT DO NOTHING`).
- Fallimento: `NakWithDelay` con attesa crescente; superato `MaxDeliver` (da metadati di consegna) il messaggio originale e il motivo vanno su `DLQ` (subject `dlq.<consumatore>`) e il messaggio è terminato (`Term`) senza bloccare i successivi. Messaggio malformato → direttamente in `DLQ`.
- Test (fixture T04): messaggio consegnato due volte → gestore eseguito una volta; gestore che fallisce sempre → in `DLQ` dopo `MaxDeliver` e i messaggi successivi sono elaborati; riavvio del consumatore a metà → nessuna perdita; messaggio malformato → `DLQ` senza ripetizioni infinite; elaborazione più lunga di `AckWait` → nessuna riconsegna spuria.

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests --filter Category=Consumer`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/shared/Messaging/tests/TriMetric.Messaging.Tests`
- Expected: tutti i test di `Messaging` verdi.

**Done when:**
- L'idempotenza è garantita dal database, non da controlli in memoria.

**Suggested commit:**
`feat(messaging): aggiunge consumatore idempotente con retry e DLQ`

### T08: Parser GPX e FIT in `ingestion`

**Status:** completed
**Covers:** REQ-007, REQ-008, REQ-010, REQ-031, REQ-035 (conteggio punti), REQ-036 (arrotondamento); AC-002, AC-014; DEC-002, DEC-012
**Depends on:** T02
**Tier:** strong — input non attendibile (XML/binario), rischio di DoS e specifica con casi ambigui.

**Action:**
- Creare il progetto `TriMetric.Ingestion` (in questo task solo parsing, ancora senza host web) e `TriMetric.Ingestion.Tests`; aggiungerli a `TriMetric.slnx`.
- `ParsedActivity` (SI: sport, inizio UTC, tempo totale e in movimento, distanza, dislivello positivo, riepilogo, giri, punti come colonne **già arrotondate** secondo DEC-012) e `IActivityFileParser`:
  - `GpxParser` con `System.Xml.Linq` e un `XmlReader` indurito (`DtdProcessing.Prohibit`, `XmlResolver = null`, limiti `MaxCharactersInDocument` e `MaxCharactersFromEntities`); estensioni Garmin `TrackPointExtension` per FC e cadenza; più tracce/segmenti uniti in ordine di tempo; distanza calcolata con haversine se assente;
  - `FitParser` con `Garmin.FIT.Sdk`: sport/sottosport, record, giri, sessione; **più sessioni** → un'attività con sport `other`, tempo e distanza sommati.
- Ogni parser controlla il `CancellationToken` ad ogni blocco di record/punti (`ct.ThrowIfCancellationRequested()`), perché FIT e XML sono sincroni; il tempo massimo è applicato dal chiamante (T10) ma qui i parser devono interrompersi entro un tempo ragionevole dopo la cancellazione.
- Mappatura sport: `running`/`run` → `run`, `cycling`/`biking`/`ride` → `ride`, `swimming`/`swim` → `swim`, altro → `other`.
- Tetto `StreamLimits.MaxPoints`: oltre il limite `TooManyPointsException`; esattamente 100.000 punti accettati.
- File di esempio in `Samples/`: `run.gpx` (con FC), `ride.fit` (con FC e potenza), un GPX e un FIT senza quota/GPS. Il FIT di esempio si produce con il codificatore del SDK in un piccolo generatore documentato nei test; i valori attesi si verificano a mano e si annotano nei test.
- Test: valori attesi (distanza, durata, dislivello, FC e potenza medie, sport); arrotondamento secondo DEC-012; GPX senza timestamp → errore di validazione; XML con DTD/entità → rifiutato; GPX malformato e FIT troncato → errore; FIT senza GPS (nuoto) → colonne di posizione `null`; FIT multisessione; 100.000 punti accettati e 100.001 rifiutati; cancellazione del token durante un file grande → il parser si interrompe.

**Test cycle:**
1. Scrivere i test con i file di esempio.
2. Run `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests --filter Category=Parsing`; atteso: fallimento.
3. Implementare i parser.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests --filter Category=Parsing`
- Expected: tutti verdi; i valori attesi coincidono con quelli verificati a mano.

**Done when:**
- Nessun parser scrive log con dati del file; la libreria FIT è usata solo dietro `IActivityFileParser`.

**Suggested commit:**
`feat(ingestion): aggiunge parser GPX e FIT con modello comune`

### T09: `ingestion` — host, persistenza e migrazione

**Status:** completed
**Covers:** REQ-012, REQ-021, REQ-023, REQ-024; DEC-001, DEC-003, DEC-006, DEC-009, DEC-014
**Depends on:** T03, T05, T06, T08
**Tier:** strong — migrazione di schema e isolamento dei dati.

**Action:**
- Host web in `TriMetric.Ingestion` (il progetto esiste già da T08: si aggiunge l'host senza toccare i parser) (`public partial class Program`): `AddServiceDefaults`, `AddDevelopmentIdentity`, `AddNatsMessaging`, `AddOutbox<IngestionDbContext>()` (senza di esso `IOutbox` non è registrato e T10 fallirebbe a runtime), `AddOutboxRelay<IngestionDbContext>`, `MapOpenApi` (`/openapi/v1.json`, **solo in Development**: deviazione consapevole registrata in T09; REQ-019 non impone l'esposizione in altri ambienti e gli script di generazione del client avviano i servizi in Development), Kestrel su `127.0.0.1:5081` (da `appsettings.json`), applicazione delle migrazioni all'avvio **solo in `Development`**.
- `IngestionDbContext` (`HasDefaultSchema("ingestion")`, `UseSnakeCaseNamingConvention()`, `MigrationsHistoryTable("__EFMigrationsHistory","ingestion")`, `ApplyOutbox`, entità `Upload`), `IDesignTimeDbContextFactory<IngestionDbContext>` con stringa di sola progettazione, stringa di connessione `ConnectionStrings:Ingestion` (ruolo `ingestion_svc`).
- Test: avvio con migrazioni su `TimescaleFixture` (ruolo `ingestion_svc`); la tabella di storia sta in `ingestion`; il ruolo non vede lo schema `activities`; test sul solo bind loopback (`appsettings.json` ha solo `http://127.0.0.1:5081`); in `Production` un endpoint protetto di prova risponde 401.
- REQ-034: l'`appsettings` del servizio non deve contenere regole di livello specifiche del provider (`Logging:Console:LogLevel:*`) che riattivino il log della query string di `Microsoft.AspNetCore.Hosting.Diagnostics` (il filtro Warning è impostato da `AddServiceDefaults`; una regola specifica del provider lo sovrascriverebbe): aggiungere un test che legge `appsettings*.json` del servizio e lo verifica. Le tracce OpenTelemetry di ASP.NET Core registrano `url.query`: nessun dato personale in query string degli endpoint (i parametri sensibili vanno nel corpo).

**Schema changes:**
- Migrazione `InitialIngestionSchema` (comando: `dotnet ef migrations add InitialIngestionSchema --project src/services/ingestion/src/TriMetric.Ingestion --output-dir Migrations`), **da creare solo dopo approvazione esplicita**:
  - tabella `ingestion.uploads`: `id` uuid PK; `user_id` uuid non nulla; `source` text non nulla; `external_id` text non nulla; `file_name` text non nulla; `size_bytes` bigint non nulla; `created_at` timestamptz non nulla;
  - indice univoco `ux_uploads_user_source_external` su (`user_id`, `source`, `external_id`);
  - tabella `ingestion.outbox`: `id` uuid PK; `subject` text non nulla; `type` text non nulla; `payload` bytea non nulla; `created_at` timestamptz non nulla; `published_at` timestamptz nulla; `attempts` integer non nulla (predefinito 0); `last_error` text nulla;
  - indice parziale `ix_outbox_unpublished` su `created_at` dove `published_at IS NULL`;
  - tabella di storia `ingestion."__EFMigrationsHistory"`;
  - operazione `EnsureSchema("ingestion")` generata da `HasDefaultSchema` (con Npgsql è un blocco `DO` con controllo di esistenza: no-op su uno schema già creato dallo script di init; è un'operazione **pianificata**, non da trattare come estranea nel controllo di ambito).
  - Nomi di PK come generati da EF con `UseSnakeCaseNamingConvention` (es. `pk_uploads`, `pk_outbox`).
  - Nessuna operazione di dati, nessun `DROP`, nessun `RENAME`.

**Test cycle:**
1. Scrivere i test (con modello EF; migrazione approvata e generata).
2. Run `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests --filter Category=Persistence`; atteso: fallimento.
3. Implementare host, contesto e factory.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests --filter Category=Persistence`
- Expected: verdi; controllo di ambito della migrazione senza operazioni non pianificate né mancanti.

**Done when:**
- Migrazione approvata, generata, controllata e non applicata a mano a nessun database.

**Suggested commit:**
`feat(ingestion): aggiunge host, DbContext e migrazione iniziale`

### T10: `ingestion` — endpoint di upload, validazione, deduplicazione e outbox

**Status:** completed (limite predefinito 32 MiB dopo la revisione; vedi `context.md`)
**Covers:** REQ-006, REQ-009, REQ-010, REQ-011, REQ-012, REQ-013, REQ-021, REQ-022, REQ-031, REQ-035; AC-001, AC-003, AC-004, AC-005, AC-009; DEC-005, DEC-006, DEC-007, DEC-012
**Depends on:** T08, T09
**Tier:** strong — upload di file non attendibili, transazione dedup+outbox, concorrenza.

**Action:**
- `POST /uploads` (`multipart/form-data`, campo `file`; con `.DisableAntiforgery()` per le minimal API con `IFormFile`): `RequireAuthorization()`, `userId` dal principal; estensione `.gpx`/`.fit` (altrimenti 415); dimensione massima `Upload:MaxBytes` (25 MB) con `MaxRequestBodySize` di Kestrel **e** controllo esplicito (413); file vuoto o non valido (400/422 con `ProblemDetails`); parsing in un task con `CancellationTokenSource` di `Upload:ParseTimeoutSeconds` (30 s): se scade → 422 (`parse-timeout`) e nessun evento; `TooManyPointsException` → 422.
- `externalId` = SHA-256 esadecimale del contenuto, `source` = `file`. In **una transazione**: se esiste `(user_id, source, external_id)` risponde `200 { status: "duplicate" }` senza evento; altrimenti inserisce `uploads` e aggiunge a `IOutbox` l'`EventEnvelope<ActivityImported>` (`Id` nuovo, `CorrelationId` dalla richiesta, valori arrotondati) e risponde `202 { uploadId, externalId, status: "accepted" }`. Due upload concorrenti dello stesso file producono una sola riga e un solo evento (vincolo di unicità + gestione del conflitto come duplicato).
- Opzioni JSON e OpenAPI: `Sport` come stringa minuscola; tipi nominati e stabili nel documento OpenAPI.
- Test con più utenti: `Dev:UserId` è unico per processo, quindi il secondo utente si ottiene con una **seconda `WebApplicationFactory`** configurata con un altro `Dev:UserId` sullo stesso database.
- Test (`WebApplicationFactory` + fixture T04): upload GPX valido → 202 e riga outbox con evento corretto; stesso file → `duplicate` e nessuna seconda riga; `.txt`, file vuoto, GPX malformato, file oltre `MaxBytes`, file con 100.001 punti → 4xx senza outbox; 100.000 punti → 202; **parser lento sostituito da un doppio di test + `ParseTimeoutSeconds` = 1 → 422 entro ~2 s e nessun evento**; senza identità (Production) → 401; utente diverso con lo stesso file → accettato separatamente; NATS fermo → 202 e pubblicazione al ritorno; due upload concorrenti → una sola riga e un solo evento; nessun log contiene coordinate o contenuto del file.

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests`; atteso: fallimento.
3. Implementare l'endpoint.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/ingestion/tests/TriMetric.Ingestion.Tests`
- Expected: tutti verdi.

**Done when:**
- Ogni scenario elencato è coperto da un test; l'endpoint non accetta `userId` dalla richiesta.

**Suggested commit:**
`feat(ingestion): aggiunge upload GPX/FIT con deduplicazione e outbox`

### T11: `activities` — host, persistenza e migrazione con hypertable

**Status:** completed (vedi `context.md`: `create_hypertable` crea da solo l'indice `activity_stream_time_idx`, non elencato in "Schema changes"; accettato)
**Covers:** REQ-016, REQ-021, REQ-023, REQ-024; AC-008; DEC-003, DEC-006, DEC-009, DEC-014
**Depends on:** T03, T07
**Tier:** strong — migrazione con hypertable, inserimento massivo e isolamento dei dati.

**Action:**
- Host web in `TriMetric.Activities` (`public partial class Program`): `AddServiceDefaults`, `AddDevelopmentIdentity`, `AddNatsMessaging`, `MapOpenApi`, Kestrel su `127.0.0.1:5082`; migrazioni all'avvio solo in `Development`.
- `ActivitiesDbContext` (`HasDefaultSchema("activities")`, snake_case, `MigrationsHistoryTable("__EFMigrationsHistory","activities")`, `ApplyInbox`, entità `Activity`, `ActivityLap`), `IDesignTimeDbContextFactory`, `ConnectionStrings:Activities` (ruolo `activities_svc`).
- `StreamWriter`: inserimento dei punti con `COPY` binario di Npgsql in `activities.activity_stream` (nomi di colonna espliciti, verificati da un test contro il modello/schema reale), con `time = start_time + offset`. Usa **la connessione e la transazione correnti del `ActivitiesDbContext`** (`Database.GetDbConnection()` e transazione corrente), la stessa dell'inbox e del gestore in T12, così attività, punti e inbox vengono confermati o annullati insieme.
- La migrazione crea la hypertable con `SELECT create_hypertable('activities.activity_stream', 'time')` (funziona grazie a DEC-014).
- Test (`TimescaleFixture`): la tabella risulta hypertable (`timescaledb_information.hypertables`); `StreamWriter` salva 50.000 punti in tempo ragionevole (< 10 s) e le colonne `null` restano `null`; `activities_svc` non vede `ingestion`; test sul solo bind loopback.

**Schema changes:**
- Migrazione `InitialActivitiesSchema` (comando: `dotnet ef migrations add InitialActivitiesSchema --project src/services/activities/src/TriMetric.Activities --output-dir Migrations`), **da creare solo dopo approvazione esplicita**:
  - tabella `activities.activities`: `id` uuid PK; `user_id` uuid non nulla; `source` text non nulla; `external_id` text non nulla; `sport` text non nulla; `start_time` timestamptz non nulla; `elapsed_seconds`, `moving_seconds`, `distance_meters` double precision non nulle; `elevation_gain_meters`, `avg_heart_rate`, `max_heart_rate`, `avg_power_watts`, `avg_cadence` double precision nulle; `created_at` timestamptz non nulla;
  - indice univoco `ux_activities_user_source_external` su (`user_id`, `source`, `external_id`); indice `ix_activities_user_start` su (`user_id`, `start_time` DESC);
  - tabella `activities.activity_laps`: `id` uuid PK; `activity_id` uuid non nulla (FK → `activities.activities.id`, ON DELETE CASCADE); `start_time` timestamptz non nulla; `elapsed_seconds`, `distance_meters` double precision non nulle; indice su `activity_id`;
  - tabella `activities.activity_stream`: `activity_id` uuid non nulla; `time` timestamptz non nulla; `latitude`, `longitude`, `altitude_meters`, `heart_rate`, `power_watts`, `cadence`, `speed_mps` double precision nulle; **nessuna chiave primaria**; indice non univoco `ix_activity_stream_activity_time` su (`activity_id`, `time`);
  - passo DDL: `SELECT create_hypertable('activities.activity_stream', 'time')`;
  - tabella `activities.inbox`: `message_id` uuid non nulla; `consumer` text non nulla; `processed_at` timestamptz non nulla; **PK composta (`consumer`, `message_id`)** (lo stesso evento può essere consumato da più consumatori dello stesso servizio);
  - tabella di storia `activities."__EFMigrationsHistory"`;
  - operazione `EnsureSchema("activities")` generata da `HasDefaultSchema` (no-op su schema esistente; operazione **pianificata**);
  - nomi di PK come generati da EF con `UseSnakeCaseNamingConvention`.
  - Nessun `DROP` né `RENAME`.

**Test cycle:**
1. Scrivere i test (migrazione approvata e generata).
2. Run `dotnet test src/services/activities/tests/TriMetric.Activities.Tests --filter Category=Persistence`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/activities/tests/TriMetric.Activities.Tests --filter Category=Persistence`
- Expected: verdi; hypertable confermata; controllo di ambito della migrazione senza differenze.

**Done when:**
- Migrazione approvata e controllata; `COPY` coerente con lo schema.

**Suggested commit:**
`feat(activities): aggiunge host, schema e hypertable per gli stream`

### T12: `activities` — consumo di `ActivityImported`

**Status:** completed (vedi `context.md`: validazione estesa con versione e tipo della busta; `sport` mancante non rilevabile, limite noto)
**Covers:** REQ-014, REQ-015, REQ-016; AC-006, AC-014; DEC-001, DEC-005
**Depends on:** T07, T11
**Tier:** strong — idempotenza e consistenza dell'inserimento.

**Action:**
- `ActivityImportedHandler : IEventHandler<ActivityImported>` registrato con `AddEventConsumer` (consumatore durevole `activities-activity-imported`, subject `ingestion.activity.imported`, `MaxDeliver` 5, backoff crescente): in una transazione inserisce l'attività con `ON CONFLICT (user_id, source, external_id) DO NOTHING` (nessun errore ritentato se esiste già), poi giri e punti (`StreamWriter`) solo se l'attività è stata inserita.
- **Validazione esplicita dell'evento** (la deserializzazione di `System.Text.Json` non la fa: `data` o `stream` possono arrivare `null`, uno `sport` mancante diventa `run`, colonne di lunghezza diversa passano): il gestore rifiuta un evento con `Data`/`Stream` nulli, colonne non lunghe quanto `TimeOffsetSeconds`, più di `StreamLimits.MaxPoints` punti o `sport` non valido; l'eccezione di validazione porta il messaggio direttamente in `DLQ`, senza ripetizioni.
- Test (fixture T04): evento valido → attività, giri e punti salvati; lo stesso evento due volte → un'attività; due eventi con lo stesso `externalId` e id diversi → una sola attività e punti non duplicati; evento con 100.000 punti → tutti salvati; evento non valido (colonne di lunghezza diversa, `data` nullo, troppi punti) → DLQ.

**Test cycle:**
1. Scrivere i test.
2. Run `dotnet test src/services/activities/tests/TriMetric.Activities.Tests --filter Category=Consumer`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/activities/tests/TriMetric.Activities.Tests --filter Category=Consumer`
- Expected: verdi.

**Done when:**
- Nessun doppione per lo stesso evento o lo stesso file; il consumo è idempotente nel database.

**Suggested commit:**
`feat(activities): consuma ActivityImported in modo idempotente`

### T13: `activities` — API di elenco e dettaglio

**Status:** completed (vedi `context.md`: `maxPoints` minimo 2 e massimo configurabile tra 1000 e 100.000; riduzione nel database; schema `ActivityListPage` in più; 400 per parametri non validi)
**Covers:** REQ-018, REQ-019, REQ-021, REQ-022; AC-007, AC-009, AC-010, AC-011; DEC-006, DEC-007
**Depends on:** T11
**Tier:** strong — isolamento per utente (REQ-022) e contratto API pubblico usato dal client generato.

**Action:**
- `GET /activities` (`sport`, `page` ≥ 1, `pageSize` 1–100, predefinito 20): ordinato per `start_time` decrescente e poi `id`, filtrato per `userId` del principal; risposta `{ items, page, pageSize, total }`.
- `GET /activities/{id}` con `maxPoints` (predefinito 1000, massimo configurabile): 404 se non esiste **o appartiene a un altro utente**; stream ridotto a passo costante mantenendo primo e ultimo punto, restituito come `StreamColumns`.
- Tutti gli endpoint con `RequireAuthorization()`; `Sport` come stringa minuscola (anche nel filtro); documento OpenAPI su `/openapi/v1.json` con tipi nominati e stabili `ActivityListItem`, `ActivityDetail`, `StreamColumns`.
- Test: paginazione senza duplicati; filtro per sport; isolamento tra due utenti (elenco e dettaglio → 404); attività con 50.000 punti con `maxPoints` 1000 → ≤ 1000 punti con primo e ultimo; fuori da Development senza identità → 401; **`/openapi/v1.json` contiene gli schemi `ActivityListItem`, `ActivityDetail` e `StreamColumns`** (REQ-019); nessun endpoint accetta `userId` dalla richiesta.

**Test cycle:**
1. Scrivere i test (dati inseriti direttamente nel database).
2. Run `dotnet test src/services/activities/tests/TriMetric.Activities.Tests --filter Category=Api`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/activities/tests/TriMetric.Activities.Tests`
- Expected: tutti verdi.

**Done when:**
- OpenAPI pubblicato e verificato da test.

**Suggested commit:**
`feat(activities): aggiunge API di elenco e dettaglio con stream ridotto`

### T14: `gateway` con YARP

**Status:** completed (vedi `context.md`: una rotta per servizio con `PathRemovePrefix`, barra finale conservata, intestazioni e `userId` rimossi, `Gateway:MaxRequestBodyBytes`, 413 non leggibile dai client per corpi grandi)
**Covers:** REQ-020; AC-001; DEC-009
**Depends on:** T03
**Tier:** strong — rimozione di intestazioni di identità e riscrittura dei percorsi (confine di sicurezza).

**Action:**
- Creare `TriMetric.Gateway` con `AddServiceDefaults`, `Yarp.ReverseProxy` e Kestrel su `localhost:5080`. Rotte: `/api/ingestion/{**remainder}` → `http://127.0.0.1:5081/{**remainder}`; `/api/activities` e `/api/activities/{**remainder}` → `http://127.0.0.1:5082/activities/{**remainder}` (trasformazioni dei percorsi). Le intestazioni di identità del client (`X-User-Id` e simili) vengono rimosse; `Authorization` passa.
- Limite del corpo allineato a `Upload:MaxBytes` (predefinito **32 MiB = 33.554.432** dopo la revisione di T10) più 1 MiB di overhead multipart per la richiesta attraverso il gateway; `/health/live`. Il controllo lato client del web (T17) usa lo stesso limite di 32 MiB.
- Test con server di prova al posto dei servizi: `POST /api/ingestion/uploads`, `GET /api/activities`, `GET /api/activities/{id}` arrivano ai backend con il percorso atteso (con e senza barra finale); un `X-User-Id` del client non raggiunge i backend; il corpo multipart arriva integro.

**Test cycle:**
1. Scrivere i test con backend simulati.
2. Run `dotnet test src/services/gateway/tests/TriMetric.Gateway.Tests`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test src/services/gateway/tests/TriMetric.Gateway.Tests`
- Expected: tutti verdi.

**Done when:**
- Nessuna logica di business nel gateway.

**Suggested commit:**
`feat(gateway): aggiunge YARP con rotte verso ingestion e activities`

### T15: Test di integrazione end-to-end

**Status:** completed
**Covers:** REQ-011, REQ-013, REQ-014, REQ-015, REQ-017, REQ-022, REQ-023, REQ-032, REQ-034, REQ-035, REQ-036; AC-001…AC-008, AC-012, AC-014
**Depends on:** T10, T12, T13, T14
**Tier:** strong — orchestrazione di contenitori reali e più servizi.

**Action:**
- Creare `tests/TriMetric.IntegrationTests` (xUnit) con le fixture di `TriMetric.TestInfrastructure` (DEC-013): i tre servizi avviati in processo con le stringhe di connessione per ruolo. Collegamento del gateway ai servizi: ogni servizio si avvia su **Kestrel con porta effimera** (non `TestServer`) e il gateway riceve le destinazioni YARP sovrascritte (`ReverseProxy:Clusters:*:Destinations:*:Address`) con gli indirizzi reali; scelta registrata qui perché `WebApplicationFactory` con `TestServer` non ha porte.
- Isolamento tra test: un solo database (quello dell'init script, con ruoli, schemi, estensione e grant reali); ogni test usa **identificativi propri** (`userId` nuovo, file con contenuto unico) e, dove serve, svuota con `TRUNCATE` le tabelle `outbox`, `inbox` e `uploads`/`activities` del proprio scenario. Niente `CREATE DATABASE` separati (perderebbero estensione e grant).
- Scenari: upload GPX → elenco → dettaglio (AC-001); FIT con FC e potenza (AC-002); doppio upload (AC-003); file non validi (AC-004); NATS fermato e riavviato con la stessa porta (AC-005); stesso evento consegnato due volte (AC-006); isolamento tra utenti (AC-007); `activities_svc` e `ingestion_svc` non leggono né scrivono nello schema dell'altro (AC-008); messaggio non elaborabile in `DLQ` e successivi elaborati (AC-012); **50.000 e 100.000 punti importati e 100.001 rifiutato** (AC-014), con evento da 100.000 punti pubblicato sotto 8 MB.
- Test sui log (REQ-034): con un provider di log in memoria, nessun messaggio contiene coordinate, valori di FC o contenuto del file.

**Test cycle:**
1. Scrivere i test di integrazione.
2. Run `dotnet test tests/TriMetric.IntegrationTests`; atteso: fallimento dove manca qualcosa (eventuali correzioni vanno nei servizi coinvolti e annotate in `context.md`).
3. Correggere i difetti emersi.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `dotnet test tests/TriMetric.IntegrationTests`
- Expected: tutti gli scenari verdi con Docker avviato.

**Done when:**
- Ogni AC elencato ha almeno un test; i test sono ripetibili e indipendenti dall'ordine.

**Suggested commit:**
`test(integration): aggiunge test end-to-end con contenitori reali`

### T16: Scaffold del frontend React

**Status:** completed
**Covers:** REQ-025, REQ-029, REQ-030, REQ-037, REQ-038, REQ-039; AC-013; DEC-007, DEC-009, DEC-015, DEC-016
**Depends on:** T10, T13
**Tier:** standard — scaffold con struttura e comandi definiti.

**Action:**
- Prerequisito umano: attivare `pnpm` (`corepack enable`; `corepack prepare pnpm@latest --activate`) e verificare `pnpm --version`; fissare `packageManager` in `package.json`. Per i test end-to-end serve anche scaricare i browser di Playwright (`pnpm -C src/web exec playwright install chromium`, un download di centinaia di MB): azione umana da confermare.
- Script `test:e2e` (Playwright) con `playwright.config.ts` e `webServer` che avvia `pnpm dev` su `http://localhost:5173`; i test intercettano `/api/*` con `page.route`.
- Creare `src/web` con Vite, React 19, TypeScript, Tailwind CSS, TanStack Router e Query, `openapi-typescript`, `openapi-fetch`, Vitest, Testing Library, Playwright, ESLint. `vite.config.ts` con proxy `/api` → `http://127.0.0.1:5080`.
- Struttura: `src/app/` (router, provider), `src/features/activities/` (con sottocartelle per le pagine), `src/shared/api/` (client e tipi generati), `src/shared/lib/` (formattazione). Regola di lint che vieta importazioni tra `features`.
- Client API: due basi, `/api/activities` per le attività e `/api/ingestion` per gli upload, con due istanze di `openapi-fetch` e tipi generati separati.
- Script `gen:api`: genera `src/shared/api/ingestion.d.ts` e `activities.d.ts` da `http://127.0.0.1:5081/openapi/v1.json` e `http://127.0.0.1:5082/openapi/v1.json` (servizi avviati); i file generati sono committati.
- Layout a due modalità (DEC-015, REQ-037): sotto 768 px navigazione in basso, contenuto a colonna e bersagli di tocco di almeno 44 px; da 768 px barra laterale. Voci di navigazione: Attività, Carica. Test del layout (sia delle due modalità sia dello scorrimento orizzontale a 375 px e a 1280 px) e delle funzioni di formattazione.
- Predisposizione al guscio nativo (DEC-015, REQ-038): `VITE_API_BASE_URL` (predefinito `/api`, documentato in `.env.example`) da cui derivano le due basi del client; `viewport-fit=cover` e margini `env(safe-area-inset-*)` nel layout; modulo `shared/lib/storage` come unico accesso a `localStorage`, con regola di lint che vieta `localStorage` e `window.open` altrove; nessun service worker né plugin PWA; instradamento servito dalla radice. Test: la base URL cambia con la variabile; il layout rispetta i margini sicuri.
- Aspetto, lingua e componenti (DEC-016, REQ-039): variabili di tema semantiche (sfondo, superficie, testo, testo secondario, bordo, accento blu, pericolo) in un solo punto, con tema chiaro e scuro automatico e interruttore a tre posizioni (Sistema, Chiaro, Scuro) salvato con `shared/lib/storage` e applicato prima del primo disegno (nessun lampo di tema sbagliato); caratteri di sistema; `i18next` e `react-i18next` con italiano e inglese (italiano predefinito, inglese se il browser non è italiano) e selettore di lingua accanto all'interruttore; tutti i testi nei file di traduzione, con un test che le due lingue abbiano le stesse chiavi; formattazione con `Intl` nella lingua corrente; componenti di base scritti a mano in `shared/ui` (nessun shadcn/ui), icone `lucide-react`.
- Non avviare il dev server con `--host` per provare da un telefono sulla rete locale: i servizi usano l'identità di sviluppo fissa; solo con un sì esplicito dell'utente e su rete fidata (DEC-015).

**Test cycle:**
1. Scrivere i test di formattazione e layout.
2. Run `pnpm -C src/web test`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `pnpm -C src/web install --frozen-lockfile; pnpm -C src/web lint; pnpm -C src/web test; pnpm -C src/web build`; con i servizi avviati `pnpm -C src/web gen:api; git diff --exit-code src/web/src/shared/api`
- Expected: tutto riuscito; la generazione non produce differenze.

**Done when:**
- Il client è generato; nessuna importazione tra `features`.

**Suggested commit:**
`feat(web): aggiunge scaffold React con client OpenAPI generato`

### T17: Web — pagina di upload

**Status:** pending
**Covers:** REQ-026, REQ-030, REQ-037; AC-001, AC-004
**Depends on:** T16
**Tier:** standard — pagina con stato e gestione errori.

**Action:**
- `features/activities/upload`: selezione di un file `.gpx`/`.fit` (validazione client di estensione e di dimensione: **oltre 32 MiB si rifiuta PRIMA di inviare**, perché attraverso il gateway il 413 di un file grande non è leggibile dal client (rilievo I1 di T14); un errore di rete durante l'invio di un file grande si mostra come «file troppo grande o connessione interrotta»; sul telefono il selettore è il normale `<input type="file">` con `accept=".gpx,.fit"`), invio con avanzamento a `POST /api/ingestion/uploads`, esito (`accepted`/`duplicate`) ed errori `ProblemDetails` mostrati chiaramente; registra l'upload accettato (`externalId`) in uno stato condiviso con l'elenco per mostrarlo "in elaborazione".
- Test (Vitest + Testing Library, server simulato): file valido → esito e stato "in elaborazione"; **avanzamento mostrato durante l'invio** (eventi di progresso simulati); `.txt` → errore client; 4xx del server → messaggio; `duplicate` → avviso.
- Test Playwright con API simulata (`page.route`) a viewport 375 px e 1280 px: `document.documentElement.scrollWidth <= window.innerWidth` sulla pagina di upload.

**Test cycle:**
1. Scrivere i test.
2. Run `pnpm -C src/web test` e `pnpm -C src/web test:e2e`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `pnpm -C src/web test; pnpm -C src/web test:e2e; pnpm -C src/web build`
- Expected: verdi.

**Done when:**
- Tutti gli esiti dell'upload sono gestiti e testati; nessuno scorrimento orizzontale a 375 px.

**Suggested commit:**
`feat(web): aggiunge pagina di upload con gestione degli errori`

### T18: Web — elenco e dettaglio attività

**Status:** pending
**Covers:** REQ-027, REQ-028, REQ-030, REQ-037; AC-001, AC-010; DEC-008, DEC-015
**Depends on:** T16, T17
**Tier:** standard — pagine di lettura con polling.

**Action:**
- Elenco: attività (data, sport, distanza, durata, dislivello) come schede sotto 768 px e come tabella da 768 px, filtro per sport, paginazione; righe "in elaborazione" per gli upload accettati non ancora presenti (corrispondenza su `externalId`); polling ogni 2 s solo finché esiste almeno un caricamento in elaborazione, con timeout oltre il quale mostra "ancora in elaborazione, riprova".
- Dettaglio: riepilogo (data, sport, distanza, durata, durata in movimento, dislivello, FC, potenza, cadenza medie) e giri; "non disponibile" per i campi assenti; 404 gestito.
- Test: elenco con filtro e paginazione; riga "in elaborazione" che diventa attività dopo il polling; dettaglio con campi mancanti; 404. Test Playwright (API simulata) a 375 px e a 1280 px su elenco e dettaglio: `scrollWidth <= innerWidth`; a 375 px le righe sono schede e c'è la navigazione in basso, a 1280 px tabella e barra laterale.

**Test cycle:**
1. Scrivere i test.
2. Run `pnpm -C src/web test` e `pnpm -C src/web test:e2e`; atteso: fallimento.
3. Implementare.
4. Rieseguire; atteso: verde.

**Verification:**
- Run: `pnpm -C src/web test; pnpm -C src/web test:e2e; pnpm -C src/web lint; pnpm -C src/web build`
- Expected: tutti verdi.

**Done when:**
- Nessun campo assente produce errori; il polling si ferma quando non serve.

**Suggested commit:**
`feat(web): aggiunge elenco e dettaglio attività con aggiornamento automatico`

### T19: Script di avvio locale e documentazione

**Status:** pending
**Covers:** REQ-005, REQ-033; AC-013; DEC-009
**Depends on:** T15, T18
**Tier:** standard — script multi-piattaforma e documento di avvio.

**Action:**
- `scripts/dev.ps1` e `scripts/dev.sh`: avviano l'infrastruttura (`docker compose -f deploy/compose.yaml up -d`, con `podman compose` se Docker manca), attendono PostgreSQL pronto, avviano `ingestion`, `activities` e `gateway` con `dotnet run` (ambiente `Development`, `Dev__UserId` impostato a un UUID fisso di esempio) e `pnpm -C src/web dev`; messaggi chiari e arresto pulito dei processi.
- `docs/getting-started.md`: prerequisiti (.NET SDK, Node, pnpm, Docker o Podman), avvio, test, rigenerazione dei tipi, nota sul `down -v` dopo modifiche allo script di inizializzazione, problemi comuni (porte occupate, Docker spento, pnpm assente).
- Aggiornare `README.md` (sezione "Avvio": percorso reale al posto di "previsto").

**Verification:**
- Run: `powershell -File scripts/dev.ps1` su una macchina con i prerequisiti; aprire `http://localhost:5173`
- Expected: infrastruttura, servizi e web partono; si può eseguire AC-001.

**Done when:**
- Documento e script coerenti tra loro e con le porte di DEC-009.

**Suggested commit:**
`docs(dev): aggiunge script di avvio locale e guida iniziale`

### T20: Verifica finale e aggiornamento dell'architettura

**Status:** pending
**Covers:** REQ-005, REQ-030, REQ-033, REQ-034, REQ-037, REQ-038; AC-001, AC-013; DEC-004, DEC-005, DEC-009, DEC-012, DEC-015
**Depends on:** T19
**Tier:** standard — verifica complessiva e allineamento documentale.

**Action:**
- Eseguire la verifica finale completa e una prova manuale di AC-001 dal browser, a 375 px e a 1280 px; provare l'avvio da zero seguendo `docs/getting-started.md`.
- Aggiornare la documentazione in base a quanto realizzato: `ARCHITECTURE/adr/0003-messaggistica-nats.md` (stato `Accettata` se il prototipo ha confermato NATS, con i problemi incontrati), `ARCHITECTURE/adr/0004` (EF Core + COPY, decisione chiusa, permessi sulle hypertable), `ARCHITECTURE/01-services.md` (un progetto per servizio, `Messaging`), `ARCHITECTURE/02-communication.md` (stream `EVENTS`/`DLQ` con prefissi espliciti, stream a colonne arrotondato, `max_payload`), `ARCHITECTURE/05-infrastructure.md` (script al posto di Aspire per ora), `ARCHITECTURE/04-frontend.md` (stato reale del front-end), `README.md` dei servizi (stato, endpoint, dati posseduti).
- Annotare in `context.md` ogni deviazione dal piano e le azioni umane compiute.

**Verification:**
- Run: vedi *Final verification*.
- Expected: tutto verde e prova manuale riuscita.

**Done when:**
- La documentazione descrive ciò che esiste davvero e nessun requisito resta senza verifica.

**Suggested commit:**
`docs(architecture): aggiorna ADR e documenti dopo lo scheletro del sistema`

### T21: Guscio nativo Capacitor (dopo lo scheletro)

**Status:** pending
**Covers:** DEC-015
**Depends on:** T20
**Tier:** standard — non fa parte della verifica finale dello scheletro; si avvia solo con un via libera esplicito dell'utente.

**Action:**
- Azioni umane da confermare prima di iniziare: per l'Android, Android Studio con SDK sul PC Windows; per l'iOS, l'account Apple Developer (99 $/anno) e una pipeline di compilazione iOS su macchine macOS nel cloud (non c'è un Mac), da scegliere insieme.
- Aggiungere Capacitor a `src/web` (`capacitor.config.ts`, progetto Android; il progetto iOS solo quando c'è la compilazione cloud), con la base URL dell'API verso un gateway raggiungibile dal telefono (`VITE_API_BASE_URL`).
- Gateway: ammettere le origini del guscio (`capacitor://localhost`, `https://localhost`) con una configurazione CORS esplicita e testata (oggi il gateway non ha CORS); nessuna origine con carattere jolly.
- Verificare sul dispositivo: elenco, dettaglio e upload di un file dal selettore di sistema, margini sicuri, tasto indietro.
- Valutare se serve almeno una funzione nativa reale per la pubblicazione (guideline 4.2 di Apple) prima di tentare gli store.

**Verification:**
- Run: build Android di debug e prova su un dispositivo o emulatore; test del gateway sulle origini ammesse.
- Expected: l'app si avvia, mostra l'elenco e carica un file; il gateway risponde solo alle origini elencate.

**Done when:**
- Android funzionante; per iOS, la pipeline scelta e lo stato documentati in `context.md`.

**Suggested commit:**
`feat(web): aggiunge guscio nativo Capacitor`

## Final verification

1. `docker compose -f deploy/compose.yaml up -d` (infrastruttura; dopo modifiche allo script di inizializzazione prima `down -v`).
2. `dotnet build TriMetric.slnx -warnaserror`
3. `dotnet test TriMetric.slnx` (inclusi `tests/TriMetric.IntegrationTests` e `tests/TriMetric.TestInfrastructure.Tests`)
4. `pnpm -C src/web install --frozen-lockfile; pnpm -C src/web lint; pnpm -C src/web test; pnpm -C src/web test:e2e; pnpm -C src/web build`
5. Con i servizi avviati: `pnpm -C src/web gen:api; git diff --exit-code src/web/src/shared/api`
6. Prova manuale: carica un GPX e un FIT dal browser; verifica elenco, dettaglio, doppio caricamento, file non valido, viste a 375 px e a 1280 px.
7. Copertura: ogni `REQ-*` e `AC-*` della tabella ha un esito verificato.

## Risks and rollback

- **Permessi delle hypertable:** verificati sperimentalmente (DEC-014); la modifica dello script di inizializzazione richiede di ricreare il volume locale (perde i dati di sviluppo).
- **SDK 11 con target `net10.0`:** pacchetti e strumenti (`dotnet-ef` 10.0.x) possono avere differenze minime; se emergono problemi, installare l'SDK 10 e aggiornare `global.json` (DEC-004).
- **Garmin.FIT.Sdk:** copertura di campi specifici (nuoto) da verificare con file reali; l'interfaccia `IActivityFileParser` permette di sostituire la libreria.
- **Dimensione degli eventi:** limite 8 MB, 100.000 punti e arrotondamento (DEC-012); se insufficiente, evento versione 2 con claim check.
- **NATS:** se il prototipo mostra attriti seri, ADR 0003 prevede il passaggio a RabbitMQ; le modifiche restano confinate in `Messaging`.
- **Migrazioni:** mai applicate a mano; il rollback è `dotnet ef migrations remove` solo su richiesta esplicita e prima di qualsiasi applicazione.
- **Portata del piano:** 21 task (T21, il guscio nativo, viene dopo lo scheletro), 12 dei quali `strong` con revisione indipendente per task; il costo di esecuzione è alto e si può rallentare fermandosi dopo T07 (messaggistica) o T15 (backend completo) per rivalutare.
- **Guscio nativo (T21):** su iPhone serve l'account Apple Developer e una compilazione su macchine macOS nel cloud (non c'è un Mac); l'Android si compila da Windows. Il gateway oggi non ha CORS. Le guideline di Apple possono respingere un'app che sia solo un sito impacchettato. Lo scheletro non dipende da nulla di tutto questo (DEC-015).
- **Rollback generale:** il lavoro è su `feature/walking-skeleton-import`; tornare a `master` annulla tutto senza effetti sull'infrastruttura (i volumi Docker di sviluppo si ricreano con `docker compose down -v`).

## Review record

- 2026-11-12 — revisione indipendente (agente separato): **Problemi trovati**. Bloccanti accolti e corretti: sovrapposizione di subject tra `EVENTS` (`*.*.*`) e `DLQ` (DEC-005); permessi per `create_hypertable` (verificati con un esperimento, DEC-014, task T04); convenzione `snake_case`, tabella di storia e `IDesignTimeDbContextFactory` (DEC-003); dimensione dell'evento nel caso peggiore con arrotondamento e test (DEC-012, T02, T08, T15); fixture di test condivise con porta NATS fissa (DEC-013, T04); test mancanti per REQ-003, REQ-010, REQ-019, REQ-020; tier alzati a `strong` per T02, T08, T13, T14 (e T09–T12); T04 e T07 vecchi spezzati (ora T04–T07 e T09–T10); comandi PowerShell corretti; controllo automatico a 375 px. Osservazioni non bloccanti recepite: schema `Deny` per il 401, `DisableAntiforgery`, limite di Kestrel, casing di `Sport`, `ON CONFLICT DO NOTHING`, `AckProgress`, intervallo di polling del relay, basi URL del client web, `127.0.0.1`, versione di pnpm e di xunit.
- 2026-11-12 — secondo passaggio (stesso revisore indipendente): un solo problema bloccante, **T09 non dipendeva da T08** (il progetto `TriMetric.Ingestion` nasce in T08); corretto con `Depends on: T03, T05, T06, T08`. Tutti i problemi del primo passaggio verificati come risolti nel contenuto dei task. Osservazioni recepite: riga REQ-020 della tabella (test di bind in T09 e T11), riferimento a T20 in DEC-004, collegamento del gateway ai servizi nei test di integrazione (Kestrel su porta effimera con destinazioni YARP sovrascritte), secondo utente con una seconda factory, isolamento dei test con un solo database e `TRUNCATE`, `COPY` sulla connessione e transazione del `DbContext`, script `test:e2e` e download dei browser di Playwright come azione umana, test dell'avanzamento in T17, divieto di `EnableSensitiveDataLogging`. Resta il suggerimento di dividere T08 in GPX e FIT se il contesto non basta (valutazione all'esecuzione). Versioni `EFCore.NamingConventions` 10.0.1 e `dotnet-ef` 10.0.x verificate su NuGet dal controller.
- 2026-11-17 — su richiesta dell'utente, adattato il front-end (T16–T18, T20) e aggiunto T21: app web unica con due modalità di layout (telefono e desktop), predisposta per un guscio nativo Capacitor che si aggiunge dopo T20 (DEC-015, REQ-037, REQ-038). Il rilievo I1 di T14 (413 non leggibile per file grandi) è recepito in T17.
- Stato finale: nessun problema bloccante aperto.
