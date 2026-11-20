# Guida iniziale

Come avviare TriMetric sul proprio computer, provarlo a mano, eseguire i test e risolvere i problemi più comuni. Il comando di avvio è uno solo per sistema operativo: gli script `scripts/dev.ps1` (Windows) e `scripts/dev.sh` (Linux, macOS, Git Bash) hanno le stesse opzioni e gli stessi messaggi.

> **Stato:** scheletro del sistema. Funzionano il caricamento di file GPX/FIT, l'elenco e il dettaglio delle attività. Mancano ancora accesso (login), analisi, pianificazione, Strava e Zepp: vedi [`README.md`](../README.md) e [`ARCHITECTURE/`](../ARCHITECTURE/README.md).

## Prerequisiti

| Strumento | Versione | Note |
|---|---|---|
| .NET SDK | **11.0.100** o più recente della stessa serie (vedi [`global.json`](../global.json)) | I progetti hanno come target `net10.0`: serve anche il runtime **ASP.NET Core 10** (`dotnet --list-runtimes` deve mostrare `Microsoft.AspNetCore.App 10.x`). |
| Node.js | **22** o più recente | `node --version` |
| pnpm | **12** (`packageManager` di [`src/web/package.json`](../src/web/package.json)) | Si installa con `npm install -g pnpm@12` oppure si attiva con `corepack enable` (poi riaprire il terminale). `pnpm --version` deve mostrare 12.x. |
| Docker o Podman | con `compose` | Docker Desktop (Windows, macOS) o Docker Engine (Linux); in alternativa Podman con `podman compose`. Il runtime deve essere **avviato**. |
| `curl` | qualsiasi | Solo per `dev.sh` (controllo dello stato dei servizi) e per le prove a mano. Su Windows PowerShell usare `curl.exe` (vedi *Problemi comuni*). |

Gli script controllano i prerequisiti prima di avviare qualcosa e, se manca qualcosa, dicono come rimediare.

## Avvio

Dalla radice del repository (gli script funzionano anche da un'altra cartella di lavoro):

```powershell
# Windows (Windows PowerShell 5.1; provato solo con questa versione)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev.ps1
```

```bash
# Linux, macOS, Git Bash
bash scripts/dev.sh
```

`-ExecutionPolicy Bypass` vale solo per quel processo: non cambia le impostazioni del computer.

Lo script, in ordine:

1. controlla i prerequisiti e che le porte da usare siano libere (in caso contrario dice quale processo le occupa);
2. avvia l'infrastruttura con `docker compose -f deploy/compose.yaml up -d` (PostgreSQL con TimescaleDB e NATS; con `podman compose` se Docker manca o non risponde) e attende che siano pronti;
3. compila **una volta sola e in sequenza** `ingestion`, `activities` e `gateway` (tre `dotnet run` paralleli si pesterebbero i file di compilazione condivisi);
4. avvia i tre servizi uno alla volta con `dotnet run --no-build`, in ambiente `Development`, e attende `/health/live` di ciascuno;
5. avvia il web (`pnpm -C src/web dev`; se manca `src/web/node_modules` esegue prima `pnpm -C src/web install --frozen-lockfile`) e attende `http://localhost:5173`;
6. stampa gli indirizzi utili e resta in primo piano.

La **prima volta** servono alcuni minuti (immagini dei contenitori, ripristino dei pacchetti NuGet e npm, compilazione); i riavvii successivi richiedono circa 20 secondi con le immagini e le dipendenze già presenti (tempo misurato su una macchina di sviluppo; varia con il carico). I log di ogni processo sono in `.dev-logs/` (un file per servizio: `ingestion.log`, `activities.log`, `gateway.log`, `web.log`; cartella ignorata da git); l'elenco dei processi avviati è in `.dev-logs/pids`.

### Fermare

- **Ctrl+C** nella finestra dello script: ferma i servizi e il web (l'albero di processi completo). L'infrastruttura resta attiva, così il riavvio è rapido.
- Da un altro terminale: `scripts\dev.ps1 -Stop` (o `bash scripts/dev.sh --stop`): fa lo stesso e lo script in primo piano termina.
- Per fermare **anche** i contenitori: `scripts\dev.ps1 -StopInfra` (o `--stop-infra`). Esegue `compose down` **senza `-v`**: i volumi `trimetric_*` e quindi i dati di sviluppo restano.
- Ferma ciò che hai avviato con lo stesso script (`dev.ps1` o `dev.sh`): ciascuno riconosce solo i propri processi e, se trova quelli dell'altro, lo dice senza toccarli.
- Se `-Stop` viene dato mentre lo script gira in una shell interattiva (non lanciato con `powershell -File`), non chiude quella shell: stampa «premi Ctrl+C nella finestra dello script».

Se i processi di un avvio precedente sono ancora attivi, lo script lo dice e non ne avvia di nuovi.

### Opzioni

| Windows (`dev.ps1`) | Linux, macOS, Git Bash (`dev.sh`) | Effetto |
|---|---|---|
| `-NoInfra` | `--no-infra` | Non avvia l'infrastruttura (PostgreSQL e NATS già attivi; lo script verifica che rispondano). |
| `-NoWeb` | `--no-web` | Non avvia il web. |
| `-Stop` | `--stop` | Ferma servizi e web avviati dallo script. |
| `-StopInfra` | `--stop-infra` | Come sopra, poi `compose down` (senza `-v`). |
| `-Runtime docker\|podman` | `--runtime docker\|podman` | Sceglie il runtime dei contenitori. Predefinito: il primo che funziona tra `docker` e `podman`; si può fissare anche con la variabile `TRIMETRIC_CONTAINER_RUNTIME`. |
| `-Help` | `--help` | Mostra l'aiuto. |

Non esiste un'opzione per esporre il web sulla rete, di proposito (vedi *Identità di sviluppo*).

### Avvio a mano (senza script)

Equivalente a quello che fanno gli script, utile per capire o per eseguire un solo pezzo. Dalla radice del repository:

```bash
docker compose -f deploy/compose.yaml up -d      # con Podman: podman compose -f deploy/compose.yaml up -d
dotnet build src/services/ingestion/src/TriMetric.Ingestion/TriMetric.Ingestion.csproj
dotnet build src/services/activities/src/TriMetric.Activities/TriMetric.Activities.csproj
dotnet build src/services/gateway/src/TriMetric.Gateway/TriMetric.Gateway.csproj
# un terminale per servizio, ambiente Development e identità di sviluppo (vedi sotto):
dotnet run --no-build --project src/services/ingestion/src/TriMetric.Ingestion
dotnet run --no-build --project src/services/activities/src/TriMetric.Activities
dotnet run --no-build --project src/services/gateway/src/TriMetric.Gateway
pnpm -C src/web dev
```

I profili `Properties/launchSettings.json` impostano già `ASPNETCORE_ENVIRONMENT=Development`. Gli script invece avviano con `--no-launch-profile` e impostano da soli `ASPNETCORE_ENVIRONMENT=Development` e `Dev__UserId`: se anche tu usi `--no-launch-profile`, impostale a mano (senza `ASPNETCORE_ENVIRONMENT=Development` il servizio parte in `Production`: niente migrazioni automatiche, niente identità di sviluppo, tutto risponde 401). Compilare prima, una volta e in sequenza, e poi avviare con `--no-build` evita le collisioni sui file `obj` condivisi quando si lanciano più servizi insieme.

## Provare a mano (AC-001)

1. Avvia tutto con lo script e apri **http://localhost:5173**.
2. Vai alla pagina **Carica** e scegli un file `.gpx` o `.fit`. Campioni pronti in [`src/services/ingestion/tests/TriMetric.Ingestion.Tests/Samples/`](../src/services/ingestion/tests/TriMetric.Ingestion.Tests/Samples/): `run.gpx` (corsa) e `ride.fit` (bici con frequenza cardiaca e potenza). Gli altri due sono casi limite dei test: `gpx-no-elevation.gpx` (senza quota, accettato) e `gpx-no-time.gpx` (senza istanti: rifiutato come non valido).
3. Dopo pochi secondi l'attività compare nell'**elenco** (riga «in elaborazione» prima, poi quella vera); aprila per vedere il **dettaglio** (distanza, durata, dislivello, frequenza cardiaca, potenza, giri).
4. Caricando di nuovo lo stesso contenuto (anche con un altro nome) il sistema risponde «duplicato» e non crea una seconda attività: i campioni risultano duplicati se sono già stati caricati nel database di sviluppo (vedi *Ripulire i dati di prova*).

Lo stesso, da riga di comando, attraverso il gateway (PowerShell: `curl.exe`, non `curl`):

```bash
curl.exe -F "file=@percorso/del/file.gpx" http://localhost:5080/api/ingestion/uploads
curl.exe http://localhost:5080/api/activities
curl.exe http://localhost:5173/api/activities    # stessa cosa attraverso il proxy del web
```

Il primo risponde `202` con `"status":"accepted"` (o `200` con `"duplicate"`); il secondo e il terzo elencano le attività con `total`. In bash si può scrivere `curl` al posto di `curl.exe`.

## Porte e indirizzi (DEC-009)

| Componente | Indirizzo | Ascolta su |
|---|---|---|
| Web (Vite) | http://localhost:5173 | `localhost`. Inoltra `/api` al gateway (nessun CORS). |
| Gateway (YARP) | http://localhost:5080 | `localhost`. `/api/ingestion/{...}` → ingestion, `/api/activities/{...}` → activities. |
| `ingestion` | http://127.0.0.1:5081 | solo `127.0.0.1` |
| `activities` | http://127.0.0.1:5082 | solo `127.0.0.1` |
| PostgreSQL | `127.0.0.1:5432` (database `trimetric`, uno schema e un ruolo per servizio) | solo `127.0.0.1` (compose) |
| NATS | `nats://127.0.0.1:4222`, monitoraggio http://127.0.0.1:8222 (`/healthz`) | solo `127.0.0.1` (compose) |

Ogni servizio espone `/health/live` e `/health/ready`. In `Development` `ingestion` e `activities` pubblicano anche il documento OpenAPI (`/openapi/v1.json`).

Tutto ascolta solo sull'indirizzo locale: i servizi .NET e il web per scelta degli script e della configurazione, PostgreSQL e NATS perché `deploy/compose.yaml` li pubblica solo su `127.0.0.1` (DEC-009). Nulla è raggiungibile dalla rete. Le password `dev_only_*` sono valori di sviluppo e NATS non ha autenticazione.

### Identità di sviluppo (DEC-006)

Non esiste ancora il login. In ambiente `Development` ogni richiesta ai servizi è autenticata come un utente fisso, `00000000-0000-4000-8000-000000000001` (`Dev:UserId` negli `appsettings.Development.json`; lo script lo imposta anche con `Dev__UserId`). Fuori da `Development` ogni richiesta protetta riceve 401. I servizi leggono l'utente solo dall'identità autenticata, mai da corpo, query o intestazioni della richiesta.

**Per questo il dev server non va mai esposto sulla rete** (`pnpm dev --host`, `vite --host`, `0.0.0.0`): chiunque raggiunga il computer potrebbe agire come quell'utente, leggere e caricare dati. Per provare su un telefono serve una decisione esplicita, una rete fidata e, in prospettiva, il login: non lo fanno gli script.

## Test

Dalla radice del repository.

```bash
dotnet build TriMetric.slnx -warnaserror      # compilazione di tutta la solution, warning come errori
dotnet test TriMetric.slnx                    # tutti i test .NET: richiede Docker o Podman avviato (Testcontainers)
```

`dotnet test TriMetric.slnx` usa contenitori veri (TimescaleDB e NATS) tramite Testcontainers e porte effimere: non serve avviare lo stack con gli script e **non conviene farlo insieme**. Tempo indicativo: circa 4 minuti (misurato; i progetti di test girano in parallelo). I progetti senza contenitori si possono eseguire da soli, ad esempio `dotnet test src/services/gateway/tests/TriMetric.Gateway.Tests`.

Web (`src/web`, [dettagli nel suo README](../src/web/README.md)):

```bash
pnpm -C src/web install --frozen-lockfile   # una tantum, o dopo un cambio di dipendenze
pnpm -C src/web lint
pnpm -C src/web typecheck
pnpm -C src/web test                        # Vitest + Testing Library
pnpm -C src/web build
```

### Test end-to-end (Playwright)

```bash
pnpm -C src/web exec playwright install chromium   # una tantum: scarica il browser (centinaia di MB)
pnpm -C src/web test:e2e
```

I test e2e avviano (o riusano) il dev server sulla porta 5173 e intercettano `/api/*`: non servono servizi né infrastruttura. Girano a 375 px e a 1280 px.

**Prova sullo stack reale** (`real-stack.spec.ts`, solo desktop): con lo stack avviato dagli script, esegui

```powershell
$env:E2E_REAL = '1'; pnpm -C src/web exec playwright test real-stack      # PowerShell
Remove-Item Env:E2E_REAL                                                    # poi togli la variabile, o resta attiva nella sessione
```

```bash
E2E_REAL=1 pnpm -C src/web exec playwright test real-stack                # bash
```

Attenzione: **ogni esecuzione aggiunge tre attività al database di sviluppo** (vedi *Ripulire i dati di prova*).

## Rigenerare i tipi dell'API

I tipi TypeScript del web sono generati dai documenti OpenAPI dei servizi e sono committati. Dopo aver cambiato le API, con `ingestion` e `activities` avviati in `Development` (basta lo script):

```bash
pnpm -C src/web gen:api
```

poi rivedi il diff di `src/web/src/shared/api/*.d.ts`.

## Database e migrazioni

- Le migrazioni si **applicano da sole all'avvio**, ma solo in `Development`. Alla prima migrazione di un database nuovo nel log compare un messaggio di livello `Error` di EF Core sulla lettura di `__EFMigrationsHistory` (la tabella non esiste ancora): è atteso e innocuo.
- Per crearne una nuova (esempio per `activities`; per `ingestion` cambia il percorso): `dotnet tool restore` (una tantum) e poi
  `dotnet ef migrations add <Nome> --project src/services/activities/src/TriMetric.Activities --output-dir Migrations`.
  Le regole (compatibilità all'indietro, rilettura operazione per operazione) sono nel [README di `ingestion`](../src/services/ingestion/README.md).
- Lo script [`deploy/postgres/init/01-schemas.sql`](../deploy/postgres/init/01-schemas.sql) (ruoli e schemi) gira **solo alla prima creazione del volume**. Se viene modificato servono `docker compose -f deploy/compose.yaml down -v` e un nuovo avvio: **`-v` cancella i volumi e quindi tutti i dati di sviluppo** (attività, stato di NATS). Gli script non lo eseguono mai.

## Comportamenti noti

- **React `StrictMode`:** nel dev server raddoppia la prima richiesta di ogni query (smonta e rimonta i componenti). In produzione non succede.
- **File molto grandi:** il limite è 32 MiB. Oltre il limite il gateway chiude la connessione senza leggere il corpo e il browser **non riesce a leggere** il `413`; per questo il web controlla la dimensione **prima** di inviare e mostra un messaggio chiaro. Chiamando `ingestion` direttamente (porta 5081) il `413` si legge normalmente.
- **Aggiornamento dell'elenco:** la riga «in elaborazione» e l'aggiornamento automatico valgono per i file caricati nella sessione corrente del browser; dopo un ricaricamento della pagina, o per un caricamento fatto altrove, ricarica l'elenco a mano.
- **Porte dei contenitori:** gli script usano porte fisse (5432, 4222, 8222). Non cambiarle in `deploy/.env` se usi gli script: i servizi hanno gli indirizzi nei loro `appsettings` e i controlli di avvio guardano quelle porte.
- **Più runtime, dati separati:** se Docker è installato ma fermo e Podman funziona, gli script passano a Podman. Podman ha volumi propri: i dati di sviluppo creati con Docker sembrano spariti (ci sono ancora, nei volumi di Docker). Per scegliere: `-Runtime docker|podman` o `TRIMETRIC_CONTAINER_RUNTIME`.
- **Git Bash:** `dev.sh` non può verificare che un PID registrato non sia stato riusato da un altro processo (lo fa su Linux, macOS e in `dev.ps1`). Con un checkout su Windows `dev.sh` deve restare con fine riga LF: lo garantisce `.gitattributes` (non rimuoverlo e non convertirlo in CRLF).
- **Il gateway non ha CORS:** il web lo raggiunge solo attraverso il proxy di Vite (`/api`).

## Problemi comuni

| Sintomo | Causa e rimedio |
|---|---|
| `Porte già occupate: porta 5173: node (PID …)` | Un altro processo usa la porta. Se è un avvio precedente dello script: `scripts\dev.ps1 -Stop`. Altrimenti chiudi il processo (Windows: `Get-NetTCPConnection -LocalPort 5173 -State Listen`; Linux/macOS: `ss -ltnp` o `lsof -iTCP:5173 -sTCP:LISTEN`). Le porte dell'infrastruttura (4222, 5432, 8222) occupate da compose già avviato: usa `-NoInfra` / `--no-infra`. |
| `Docker è installato ma non risponde` | Docker Desktop è spento o non ha finito di avviarsi: avvialo, attendi l'icona «pronto» e riprova. Con Podman: `podman machine start`. |
| `Nessun runtime di contenitori trovato` | Installa Docker Desktop o Podman (con `compose`). |
| `pnpm non trovato` | `npm install -g pnpm@12` oppure `corepack enable`; riapri il terminale. Se pnpm è appena stato installato e non si trova, riapri anche l'editor/terminale che lancia lo script (il `PATH` si legge all'avvio). |
| `Serve Node 22 o più recente` | Aggiorna Node da https://nodejs.org (o con il tuo gestore di versioni). |
| `.NET SDK non compatibile con global.json` / manca il runtime 10 | Installa l'SDK indicato in `global.json` e il runtime ASP.NET Core 10 (https://dotnet.microsoft.com/download). |
| PowerShell: `curl` non si comporta come atteso | In Windows PowerShell 5.1 `curl` è un alias di `Invoke-WebRequest`: usa `curl.exe`. |
| `scripts\dev.ps1` si chiude subito o dice che `running scripts is disabled` | Avvialo come nella sezione *Avvio* (`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\dev.ps1`). |
| Percorsi che non si trovano (`src/web`, `deploy/compose.yaml`) | I comandi di questa guida sono scritti per la **radice del repository** (la cartella che contiene `TriMetric.slnx`); gli script invece funzionano da qualunque cartella. |
| Un servizio non risponde entro il tempo massimo | Guarda `.dev-logs/<servizio>.log`. Cause tipiche: PostgreSQL non raggiungibile, porta occupata, errore di compilazione (mostrato dallo script). |
| Il caricamento resta «in elaborazione» | `activities` non riceve gli eventi: controlla `.dev-logs/activities.log` e che NATS risponda su http://127.0.0.1:8222/healthz. |
| Il caricamento di un campione risponde «duplicato» | Quel contenuto è già nel database di sviluppo: vedi *Ripulire i dati di prova*. |

## Ripulire i dati di prova

I dati di sviluppo stanno nei volumi `trimetric_postgres-data` e `trimetric_nats-data`. Non c'è (per ora) un comando per cancellare solo le attività: per ripartire da zero, **perdendo tutti i dati di sviluppo**:

```bash
scripts/dev.sh --stop-infra                       # o scripts\dev.ps1 -StopInfra
docker compose -f deploy/compose.yaml down -v     # cancella i volumi (con Podman: podman compose ...)
```

e poi riavvia con lo script (le migrazioni ricreano le tabelle). `down -v` fa parte di *questa* procedura, mai degli script di avvio.

## Struttura del repository in breve

```text
.
├── README.md                Panoramica e avvio
├── docs/getting-started.md  Questa guida
├── scripts/                 dev.ps1 e dev.sh (avvio locale)
├── ARCHITECTURE/            Architettura e decisioni (ADR)
├── deploy/                  compose.yaml (PostgreSQL/TimescaleDB, NATS), init del database, configurazione NATS
├── src/
│   ├── services/            ingestion, activities, gateway (con test) e i servizi ancora da fare
│   ├── shared/              contracts, ServiceDefaults, Messaging
│   └── web/                 Front-end React (vedi il suo README)
└── tests/                   Infrastruttura di test condivisa e test di integrazione end-to-end
```

Ogni servizio ha un proprio `README.md` (responsabilità, API, dati posseduti, limiti noti): [`ingestion`](../src/services/ingestion/README.md), [`activities`](../src/services/activities/README.md), [`gateway`](../src/services/gateway/README.md). Le decisioni architetturali sono in [`ARCHITECTURE/`](../ARCHITECTURE/README.md).
