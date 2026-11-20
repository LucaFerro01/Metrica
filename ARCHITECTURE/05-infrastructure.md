# 05 · Infrastruttura

## Contenitori: Docker o Podman

Obiettivo: lo stesso progetto parte con **Docker** o con **Podman**, senza modifiche ([ADR 0006](adr/0006-contenitori-docker-podman.md)).

Regole per restare neutri:

- Immagini in formato **OCI**, costruite da `Dockerfile` standard (Podman li legge).
- Orchestrazione locale con un unico **`compose.yaml`** in formato Compose standard, avviabile con `docker compose` o `podman compose`.
- Nessun uso di funzioni esclusive di un solo runtime (socket di Docker montato nei contenitori, `docker.sock`, estensioni specifiche).
- Nomi di immagine completi (con registro), perché Podman non assume Docker Hub per i nomi brevi.
- Volumi con attenzione ai permessi: Podman *rootless* mappa gli utenti in modo diverso; si usa l'opzione di etichetta `:Z` dove serve su Linux con SELinux.
- Nei Dockerfile, utente non privilegiato.

## Sviluppo locale

**Realizzato (DEC-009):** solo l'infrastruttura nei contenitori. Database e bus partono da `deploy/compose.yaml` (PostgreSQL con TimescaleDB e NATS); i servizi .NET e il frontend si eseguono dal terminale. Un solo comando per sistema operativo avvia tutto e lo ferma:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev.ps1     # Windows
```

```bash
bash scripts/dev.sh                                                     # Linux, macOS, Git Bash
```

Gli script `scripts/dev.ps1` e `scripts/dev.sh` (stesse opzioni e stessi messaggi, guida in [`docs/getting-started.md`](../docs/getting-started.md)): controllano i prerequisiti (SDK da `global.json` con il runtime ASP.NET Core 10, Node 22, pnpm 12, Docker o Podman) e le porte; avviano l'infrastruttura con `compose up -d`; compilano `ingestion`, `activities` e `gateway` in sequenza e li avviano uno alla volta con `dotnet run --no-build` in `Development` (tre compilazioni parallele si pesterebbero i file condivisi); avviano il web con `pnpm dev`; scrivono i log in `.dev-logs/` (ignorata da git) e registrano i processi avviati in `.dev-logs/pids`. Ctrl+C o `-Stop`/`--stop` fermano servizi e web; `-StopInfra`/`--stop-infra` fa anche `compose down` **senza** `-v`. Gli script non cancellano mai i volumi. **Provati solo su Windows**: `dev.ps1` con Windows PowerShell 5.1 e `dev.sh` con Git Bash; Linux, macOS, PowerShell 7, Podman e `shellcheck` non sono stati provati.

**Alternative non adottate:**

1. **Tutto nei contenitori:** compose avvia anche i servizi, per provare l'insieme come in produzione. **Previsto**, non realizzato: non esistono Dockerfile né immagini dei servizi.
2. **.NET Aspire** **[Ipotesi]** come orchestratore di sviluppo: un progetto *AppHost* descrive servizi, database e bus, li avvia insieme, fornisce una dashboard con log, tracce e metriche e collega automaticamente le stringhe di connessione. **Non adottato nello scheletro** (con SDK 11 e target `net10.0` avrebbe aggiunto attrito a uno scheletro già ampio, DEC-009); resta un'ipotesi da valutare.

### Porte, indirizzi e identità di sviluppo

Tutto è raggiungibile solo dal computer locale: PostgreSQL e NATS sono pubblicati da compose solo su `127.0.0.1` (DEC-009; prima erano su tutte le interfacce), i servizi .NET ascoltano solo su `127.0.0.1` (Kestrel da `appsettings.json`, non da `ASPNETCORE_URLS`), il gateway e il web su `localhost`.

| Componente | Indirizzo | Ascolta su |
|---|---|---|
| Web (Vite) | `http://localhost:5173` (porta fissa; inoltra `/api` al gateway, nessun CORS) | `localhost` |
| `gateway` | `http://localhost:5080` | `localhost` |
| `ingestion` | `http://127.0.0.1:5081` | solo `127.0.0.1` |
| `activities` | `http://127.0.0.1:5082` | solo `127.0.0.1` |
| PostgreSQL | `127.0.0.1:5432` (database `metrica`) | solo `127.0.0.1` (compose) |
| NATS | `nats://127.0.0.1:4222`, monitoraggio `http://127.0.0.1:8222` | solo `127.0.0.1` (compose) |

Le porte dei contenitori si possono cambiare con `deploy/.env` (si parte da `deploy/.env.example`), ma gli script e gli `appsettings` dei servizi usano quelle fisse: non cambiarle se si usano gli script.

**Identità di sviluppo** (DEC-006): in `Development` ogni richiesta ai servizi è autenticata come l'utente fisso `Dev:UserId` (`00000000-0000-4000-8000-000000000001` negli `appsettings.Development.json`); fuori da `Development` ogni richiesta protetta riceve 401. Per questo il dev server **non va mai avviato con `--host`** (o esposto sulla rete): chiunque raggiunga il computer agirebbe come quell'utente; gli script non offrono un'opzione per farlo. Le password del database (`dev_only_*`) sono valori di sviluppo e NATS non ha autenticazione.

**Dopo una modifica a `deploy/postgres/init/01-schemas.sql`** (ruoli, schemi, permessi) serve `docker compose -f deploy/compose.yaml down -v` e un nuovo avvio: lo script gira solo alla prima creazione del volume e `-v` **cancella i dati di sviluppo** (attività, stato di NATS). Non lo fa mai uno script di avvio.

**`.gitattributes`:** fissa il fine riga dei file che CRLF romperebbe: `*.sh` e i file `deploy/**/*.sql` e `deploy/**/*.conf` in LF (montati nei contenitori Linux o eseguiti da Git Bash), `scripts/dev.ps1` in CRLF. Con `core.autocrlf=true` (impostazione di Git per Windows) un checkout trasformerebbe `dev.sh` in CRLF e Git Bash fallirebbe; non rimuoverlo.

## Contenitori

| Contenitore | Immagine | Stato |
|---|---|---|
| `postgres` | `docker.io/timescale/timescaledb:latest-pg16` (tag non fissato a una versione precisa) | **Esiste** (`deploy/compose.yaml`). Volume persistente `postgres-data`, script di creazione schemi e ruoli per servizio montato in `docker-entrypoint-initdb.d`, controllo di salute con `pg_isready` |
| `nats` | `docker.io/library/nats:2` (tag major, non fissato) | **Esiste**. JetStream con `max_payload` di 8 MiB da `deploy/nats/nats-server.conf`, volume persistente `nats-data`, nessun controllo di salute in compose (gli script usano `/healthz` del monitoraggio) |
| `gateway`, `identity`, `ingestion`, `activities`, `analytics`, `training-plan` | Immagine .NET per servizio | **Previsto**: nessun Dockerfile. Si costruiranno con build in più fasi (immagine finale minima) |
| `web` | File statici serviti da un web server leggero | **Previsto**: nginx o Caddy con proxy verso il gateway; oggi solo `pnpm dev` (e `pnpm build` produce `dist/`) |

Provato solo con Docker: Podman e gli script con Podman non sono stati provati (ADR 0006).

## Configurazione e segreti

- Configurazione tramite **variabili d'ambiente** e file `.env` locali **non versionati**; nel repository solo `.env.example` (`deploy/.env.example` per compose, `src/web/.env.example` per `VITE_API_BASE_URL`).
- Le stringhe di connessione di sviluppo dei servizi stanno in `appsettings.Development.json` (credenziali `dev_only_*`) e **non** negli altri ambienti, dove vanno fornite dalla configurazione.
- Segreti (secret di Strava, chiavi di cifratura) mai nel codice né nelle immagini. In produzione tramite i segreti del runtime o un gestore dedicato **[Ipotesi]**.
- Un tunnel (come `ngrok` nel PoC) o un dominio pubblico serve in sviluppo per ricevere i webhook di Strava **[Ipotesi]** (Strava non è ancora integrato).

## Osservabilità

- **OpenTelemetry** in ogni servizio implementato (da `ServiceDefaults`): tracce solo di ASP.NET Core e HttpClient, metriche di ASP.NET Core, HttpClient e runtime; **nessuna strumentazione di EF Core né di NATS**; l'esportazione OTLP si attiva solo se è configurato un endpoint (`OTEL_EXPORTER_OTLP_ENDPOINT` o `Otlp:Endpoint`), altrimenti nessuna esportazione. Nessun collettore è avviato da compose. Log strutturati JSON sulla console con il `correlationId` (header `X-Correlation-Id` o generato) come scopo di log; nei log niente coordinate, frequenza cardiaca, contenuto o nomi dei file (REQ-034).
- Le tracce seguono una richiesta dal gateway agli eventi che ne derivano (il contesto di traccia viaggia nella busta degli eventi) **[Ipotesi]**: nella busta c'è il `correlationId`, non un contesto di traccia, e il consumatore non lo riprende ancora nei log.
- In sviluppo: dashboard di Aspire (non adottato), oppure un collettore OTel con Grafana/Tempo/Loki/Prometheus in un profilo compose opzionale **[Ipotesi]**.
- Endpoint di **salute** (`/health/live`, `/health/ready`) su ogni servizio: `live` non controlla nulla, `ready` controlla il database (non NATS, REQ-013; il gateway non ha controlli). Li usano gli script per attendere l'avvio; compose ha solo il controllo di `postgres`.

## Integrazione continua

**Stato attuale:** non esiste alcuna pipeline di integrazione continua. I test di integrazione con contenitori reali per database e bus (Testcontainers) esistono già e si eseguono in locale con `dotnet test Metrica.slnx` (richiede Docker o Podman avviato; il tempo di circa 4 minuti è una misura del registro di sviluppo, non ricavata dal codice; vedi [`docs/getting-started.md`](../docs/getting-started.md)).

**[Ipotesi]** (pipeline futura): una pipeline per servizio, avviata solo quando cambiano i suoi file: compilazione, test, costruzione dell'immagine. Piattaforma da scegliere (es. GitHub Actions).

## Produzione

Da decidere. Opzioni realistiche per un singolo sviluppatore, dalla più semplice: un server con compose; un server con Podman e `systemd`/Quadlet; un cluster Kubernetes gestito (più impegnativo, valore didattico alto). Non è una decisione da prendere ora: l'architettura non dipende da questa scelta.
