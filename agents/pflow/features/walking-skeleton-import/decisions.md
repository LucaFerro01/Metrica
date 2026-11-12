---
schema_version: 1
updated_at: 2026-11-12
---

# Technical Decisions: Scheletro del sistema e import di file GPX/FIT

### DEC-001: Client NATS ufficiale con outbox e inbox scritte nel progetto `Messaging`
**Decision:** Usare `NATS.Net` (client ufficiale con JetStream) e implementare outbox, relay, inbox e consumatore con retry e DLQ in un progetto condiviso `TriMetric.Messaging`, senza librerie di messaggistica di terze parti.
**Rationale:** Obiettivo di apprendimento del pattern; il client ufficiale è mantenuto e supporta JetStream; poche centinaia di righe sono gestibili. La deduplicazione lato server usa l'id dell'evento come `Nats-Msg-Id`.
**Alternatives considered:** Wolverine (meno codice ma molta magia, supporto NATS meno maturo e si impara la libreria, non il pattern); outbox/inbox duplicate in ogni servizio (copie che divergono).
**Consequences:** Nuovo codice condiviso oltre a `contracts` e `ServiceDefaults`: il vincolo dei requisiti è esteso a `Messaging`, che contiene solo infrastruttura e nessuna logica di dominio. Il progetto non deve dipendere da alcun servizio.

### DEC-002: `Garmin.FIT.Sdk` per FIT, `System.Xml.Linq` per GPX, dietro `IActivityFileParser`
**Decision:** Leggere i file FIT con il pacchetto ufficiale `Garmin.FIT.Sdk` e i GPX con `System.Xml.Linq`, entrambi dietro l'interfaccia `IActivityFileParser` in `ingestion`.
**Rationale:** SDK ufficiale completo e aggiornato (21.221.0, .NET Standard 2.0); nessuna dipendenza per GPX.
**Alternatives considered:** libreria di terze parti (copertura dei campi incerta); decoder scritto in proprio (sproporzionato).
**Consequences:** Dipendenza dalla licenza del SDK Garmin, da rileggere prima di distribuire il prodotto ad altri. L'interfaccia permette di sostituire la libreria.

### DEC-003: EF Core 10 per tabelle e migrazioni, `COPY` binario per gli stream
**Decision:** Usare EF Core 10 con `Npgsql.EntityFrameworkCore.PostgreSQL` per tabelle relazionali, outbox/inbox e migrazioni; inserire i punti degli stream nella hypertable con `COPY` binario di Npgsql; creare l'hypertable con SQL dentro la migrazione.
**Rationale:** Produttività e migrazioni versionate dove servono, prestazioni per decine di migliaia di punti.
**Alternatives considered:** solo EF Core (inserimenti lenti); solo Dapper con DbUp (più SQL scritto a mano, niente EF).
**Consequences:** Due modi di accesso ai dati, separati per ruolo. Nomi in `snake_case` ottenuti con il pacchetto `EFCore.NamingConventions` 10.0.1 (`UseSnakeCaseNamingConvention()`), così tabelle e colonne coincidono con quelle dichiarate nei piani e con i nomi usati dal `COPY` (che usa nomi di colonna espliciti, verificati da un test contro il modello EF). Le migrazioni EF usano lo schema del servizio come schema predefinito (`HasDefaultSchema`) e `MigrationsHistoryTable("__EFMigrationsHistory", "<schema>")`. Per creare le migrazioni ogni `DbContext` ha una `IDesignTimeDbContextFactory<T>` con stringa di connessione fissa di sola progettazione, quindi `dotnet ef migrations add` non richiede NATS, identità né un host avviato; serve il pacchetto `Microsoft.EntityFrameworkCore.Design` 10.0.x (`PrivateAssets=all`). La migrazione dell'hypertable contiene SQL grezzo dichiarato in `Schema changes`. Le migrazioni non si applicano mai a mano su un database: in sviluppo si applicano all'avvio del servizio (REQ-024).

### DEC-004: Layout della solution e versioni
**Decision:** Una solution `TriMetric.slnx` alla radice; target `net10.0` per tutti i progetti; `global.json` che fissa l'SDK 11.0.100 installato con `rollForward: latestFeature`; `Directory.Build.props` (nullable, warning come errori, `LangVersion` predefinito) e `Directory.Packages.props` con gestione centralizzata delle versioni dei pacchetti. Ogni servizio è **un solo progetto** `TriMetric.<Servizio>` in `src/services/<servizio>/src/` con test in `src/services/<servizio>/tests/TriMetric.<Servizio>.Tests/`; i progetti condivisi sono in `src/shared/contracts/`, `src/shared/ServiceDefaults/`, `src/shared/Messaging/`; i test di integrazione in `tests/TriMetric.IntegrationTests/`.
**Rationale:** Una sola versione per pacchetto; l'SDK 11 compila per `net10.0` ed è l'unico installato più recente dell'SDK 9; un solo progetto per servizio è sufficiente per lo scheletro.
**Alternatives considered:** quattro progetti per servizio come in `ARCHITECTURE/01-services.md` (rimandato: si adotta quando c'è logica di dominio consistente); `.sln` classico (formato vecchio); target `net11.0` (non LTS).
**Consequences:** Deviazione consapevole dal layout a quattro progetti descritto in `ARCHITECTURE/01-services.md`: la documentazione viene aggiornata in T20. Se si installa l'SDK 10, `global.json` si aggiorna senza altri cambi.

### DEC-005: Contratti come record C#, stream JetStream `EVENTS` e `DLQ`, stream dati a colonne
**Decision:** Definire busta ed eventi come `record` C# in `TriMetric.Contracts` (JSON `System.Text.Json`, camelCase, enum come stringhe). Gli eventi viaggiano sullo stream JetStream `EVENTS` con subject `<servizio>.<entità>.<evento>` (es. `ingestion.activity.imported`); lo stream `EVENTS` è dichiarato con i prefissi **espliciti** dei servizi (`identity.>`, `ingestion.>`, `activities.>`, `analytics.>`, `training-plan.>`) e **non** con un pattern generico come `*.*.*`, che si sovrapporrebbe a `dlq.>` (JetStream rifiuta stream con subject sovrapposti). I messaggi falliti vanno nello stream `DLQ` con subject `dlq.<consumatore>` (nome del consumatore senza punti). Entrambi gli stream sono creati in modo idempotente da `AddNatsMessaging`; ogni consumatore crea il proprio consumatore durevole. I valori dell'enum `Sport` sono stringhe minuscole (`run`, `ride`, `swim`, `other`) ovunque: JSON degli eventi, API, OpenAPI e database. I punti dello stream sono nell'evento in **formato a colonne** (un array per campo, `null` per i campi assenti), con un massimo di 100.000 punti.
**Rationale:** Allineato a `ARCHITECTURE/02-communication.md`; il formato a colonne è circa tre volte più compatto di un array di oggetti.
**Alternatives considered:** schema JSON generato (rimandato); stream per attività in un object store o in più eventi (vedi DEC-012).
**Consequences:** Il limite di 100.000 punti e di 8 MB (DEC-012) fanno parte del contratto; un cambio incompatibile richiede una nuova `version` dell'evento.

### DEC-006: Identità di sviluppo in `ServiceDefaults`
**Decision:** Fornire in `ServiceDefaults` un'estensione `AddDevelopmentIdentity()` che, **solo** con ambiente `Development`, autentica ogni richiesta come l'utente configurato in `Dev:UserId` (UUID). Fuori da `Development` si registra come schema predefinito un gestore `Deny` che risponde sempre 401 (non si lascia il servizio senza schema: `RequireAuthorization()` senza schema produce un'eccezione, non un 401). I servizi leggono `userId` esclusivamente dal `ClaimsPrincipal`, mai da corpo, query o intestazioni della richiesta.
**Rationale:** Soddisfa REQ-021 e AC-009 e prepara l'arrivo di `identity` (basterà sostituire lo schema di autenticazione).
**Alternatives considered:** intestazione `X-User-Id` iniettata dal gateway (fiducia nelle intestazioni, difficile da blindare); autenticazione reale subito (richiede `identity`, fuori ambito).
**Consequences:** L'identità di sviluppo non deve mai attivarsi in altri ambienti: c'è un test che lo verifica. Ogni endpoint dei servizi usa `RequireAuthorization()` e `userId` dal principal.

### DEC-007: OpenAPI con `Microsoft.AspNetCore.OpenApi` e client TypeScript generato
**Decision:** Pubblicare i documenti OpenAPI con `Microsoft.AspNetCore.OpenApi` (10.0.x) e generare nel web i tipi con `openapi-typescript` e usare `openapi-fetch` come client; gli schemi generati sono committati.
**Rationale:** Strumenti leggeri e standard, tipi sempre allineati al backend (REQ-029).
**Alternatives considered:** Orval o NSwag (più pesanti, genere codice più voluminoso).
**Consequences:** Serve rigenerare i tipi (`pnpm gen:api`) quando cambiano le API; lo schema committato va controllato in revisione.

### DEC-008: Aggiornamento della lista con polling
**Decision:** Nel web, l'elenco delle attività si aggiorna con il polling di TanStack Query (intervallo di 2 secondi) finché esiste un caricamento in elaborazione; altrimenti non c'è polling continuo. Server-Sent Events rimandati.
**Rationale:** Sufficiente per REQ-027 e facile da testare.
**Alternatives considered:** SSE (più componenti lato server, non necessario ora).
**Consequences:** Un ritardo massimo percepito di circa 2 secondi.

### DEC-009: Avvio locale con script, senza Aspire; porte e proxy
**Decision:** Avvio locale con `scripts/dev.ps1` e `scripts/dev.sh` (infrastruttura con compose, poi i servizi con `dotnet run`, poi il web con `pnpm dev`); porte gateway 5080, ingestion 5081, activities 5082, web 5173; i servizi ascoltano solo su `127.0.0.1`, il gateway su `localhost`; il dev server di Vite inoltra `/api` al gateway (nessun CORS).
**Rationale:** Aspire resta un'ipotesi da valutare dopo: con SDK 11 e target `net10.0` aggiungerebbe attrito a uno scheletro già ampio.
**Alternatives considered:** .NET Aspire AppHost (rimandato); tutto nei contenitori (fuori ambito, vedi requisiti).
**Consequences:** `ARCHITECTURE/05-infrastructure.md` conserva Aspire come ipotesi. Gli script devono funzionare su Windows e su shell POSIX.

### DEC-010: Strumenti di test
**Decision:** xUnit per i test .NET, Testcontainers (`Testcontainers.PostgreSql` e `Testcontainers.Nats` 4.16.0) per l'integrazione, `Microsoft.AspNetCore.Mvc.Testing` per i test di API; Vitest e Testing Library per il web.
**Rationale:** Standard dell'ecosistema, usano contenitori reali come richiesto da REQ-032.
**Alternatives considered:** database in memoria o SQLite (non coprono hypertable e permessi); NUnit (nessun vantaggio).
**Consequences:** I test di integrazione richiedono un runtime di contenitori avviato (Docker o Podman).

### DEC-011: Nessun riferimento di ticket
**Decision:** Le migrazioni EF Core non hanno prefisso `US<numero>` (nome es. `InitialIngestionSchema`; il timestamp lo aggiunge EF) e i commit conventional non portano `#ref`; `tracking_ref` resta `null`.
**Rationale:** Il progetto non ha un sistema di ticket e il riferimento non va inventato.
**Alternatives considered:** un numero fisso (artificiale).
**Consequences:** Se in futuro si adotta un tracker, i nomi dei nuovi sviluppi lo useranno; quelli già creati non si rinominano.

### DEC-012: Stream nell'evento a colonne con limite NATS a 8 MB
**Decision:** L'evento `ActivityImported` contiene lo stream completo in formato a colonne; il server NATS è configurato con `max_payload` di 8 MB; `ingestion` rifiuta file con più di 100.000 punti (errore 4xx).
**Rationale:** Il limite predefinito di 1 MB non regge attività di molte ore (circa 18.000 punti in 5 ore già superano 1 MB in JSON con oggetti). L'evento autosufficiente evita chiamate tra servizi. Con doubles a precisione piena (15-17 cifre) 100.000 punti × 8 colonne supererebbero 8 MB (~15 MB): `ingestion` **arrotonda** i valori prima di costruire l'evento — tempo a 0,001 s, latitudine e longitudine a 7 decimali (circa 1 cm), quota a 0,1 m, velocità a 0,001 m/s, frequenza cardiaca, potenza e cadenza a numeri interi. Nel caso peggiore (tutte e 8 le colonne, valori con il massimo di cifre, segno incluso) restano circa 64 caratteri per punto, cioè circa 6,4 MB per 100.000 punti, sotto gli 8 MB.
**Alternatives considered:** claim check con JetStream Object Store (più componenti e ciclo di vita dei dati da gestire, nessun limite); stream spezzato in più eventi (ordine e idempotenza complessi).
**Consequences:** Se servono file più grandi, si passa all'Object Store con una nuova `version` dell'evento, senza toccare le altre parti. La configurazione NATS in `deploy/` fa parte del contratto operativo. L'arrotondamento è parte del contratto di `ActivityImported` e un test misura il caso peggiore (tutte le colonne, valori massimi) sotto 8 MB; un test di integrazione importa davvero 100.000 punti.

### DEC-013: Infrastruttura di test condivisa
**Decision:** Creare un progetto `tests/TriMetric.TestInfrastructure` con fixture riutilizzabili: `TimescaleFixture` (immagine `docker.io/timescale/timescaledb:latest-pg16`, con lo script `deploy/postgres/init/01-schemas.sql` montato come `docker-entrypoint-initdb.d`, così ruoli e schemi di test sono quelli reali) e `NatsFixture` (immagine `docker.io/library/nats:2` con `deploy/nats/nats-server.conf` e **porta host fissa** scelta una volta, in modo che il riavvio del contenitore non cambi la porta). Tutti i test che usano contenitori usano queste fixture.
**Rationale:** Con la porta mappata casuale un riavvio del contenitore NATS cambierebbe l'indirizzo e il test AC-005 non potrebbe riconnettersi; usare gli script reali evita di verificare ruoli e permessi diversi da quelli di sviluppo.
**Alternatives considered:** fixture duplicate in ogni progetto di test (divergono); schemi creati dai test (non verificano i permessi reali).
**Consequences:** Progetto di test condiviso (non di produzione); i test richiedono Docker o Podman avviato.

### DEC-014: Ruoli di database e permessi per le hypertable
**Decision:** Lo script `deploy/postgres/init/01-schemas.sql` concede a ogni ruolo di servizio `USAGE` sullo schema `public` (dove risiede l'estensione `timescaledb`) e imposta il suo `search_path` a `<schema>, public`; le migrazioni creano le hypertable con `SELECT create_hypertable(...)`. `public` resta senza `CREATE` per i ruoli.
**Rationale:** Verificato con un esperimento sul database di sviluppo il 2026-11-12: senza il grant `create_hypertable` non viene trovata (non qualificata) o dà `permission denied for schema public` (qualificata); con `GRANT USAGE ON SCHEMA public` e `search_path = activities, public` la creazione riesce e il ruolo può inserire. Il ruolo resta isolato dagli schemi degli altri servizi.
**Alternatives considered:** concedere ai ruoli privilegi più ampi o usare il superutente nelle migrazioni (rompe l'isolamento); creare le hypertable fuori dalla migrazione (migrazioni non autosufficienti).
**Consequences:** Lo script di inizializzazione gira solo alla prima creazione del volume: dopo la modifica servono `docker compose -f deploy/compose.yaml down -v` e un nuovo avvio (azione umana, perde i dati di sviluppo). Una tabella hypertable non ha chiave primaria che escluda la colonna `time`: si usa un indice non univoco.
