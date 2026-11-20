# Architettura

Documentazione delle scelte architetturali. Il [README principale](../README.md) descrive il prodotto; qui si spiega **come è costruito e perché**.

## Indice

| File | Contenuto |
|---|---|
| [00-overview.md](00-overview.md) | Contesto, obiettivi di qualità, vincoli, vista d'insieme |
| [01-services.md](01-services.md) | Servizi, responsabilità, dati posseduti, API |
| [02-communication.md](02-communication.md) | Comunicazione sincrona e asincrona, eventi, affidabilità |
| [03-data.md](03-data.md) | Database, schemi, serie temporali, migrazioni |
| [04-frontend.md](04-frontend.md) | Frontend React, PWA, micro-frontend |
| [05-infrastructure.md](05-infrastructure.md) | Contenitori (Docker/Podman), script di sviluppo (Aspire: ipotesi), CI, osservabilità |
| [06-security.md](06-security.md) | Autenticazione, token, dati personali |
| [07-integrations.md](07-integrations.md) | Strava, Zepp, FIT/GPX, Bryton |
| [08-analytics.md](08-analytics.md) | Modello delle metriche e delle stime |
| [adr/](adr/) | Registro delle decisioni, una per file |

## Come si usa questa cartella

- Le **decisioni** vivono negli ADR (*Architecture Decision Record*) in `adr/`. Ogni ADR ha stato `Proposta`, `Accettata`, `Superata` o `Scartata`, e non si riscrive: se cambia idea, se ne crea uno nuovo che sostituisce il precedente.
- I file numerati descrivono il **disegno** dell'architettura e, dove esiste, lo **stato dello scheletro del sistema** (import di file GPX/FIT, 2026-11-20); rimandano agli ADR per le motivazioni. Le tabelle di stato sono in [01-services.md](01-services.md) (servizi e progetti) e in [06-security.md](06-security.md) (sicurezza).
- Vocabolario usato nei file numerati:
  - **Implementato** / **Realizzato** / **Provato**: esiste nel codice e c'è almeno un test che lo verifica (se manca la prova, il testo lo dice).
  - **Previsto**: fa parte del disegno ma non è stato realizzato (non c'è codice).
  - **[Ipotesi]**: scelta non ancora verificata né decisa, da confermare con un prototipo o con l'uso.
  - Ciò che è una scelta già presa è scritto al presente.
- Prefissi nei documenti: **DEC-nnn** (decisione tecnica), **REQ-nnn** (requisito), **AC-nnn** (scenario di accettazione) e **Tnn** (task del piano) vengono dal registro di sviluppo dello scheletro, in `agents/pflow/features/walking-skeleton-import/` (`decisions.md`, `requirements.md`, `plan.md`, `context.md`).

## Decisioni prese

| ADR | Decisione | Stato |
|---|---|---|
| [0001](adr/0001-backend-csharp-dotnet.md) | Backend in C# / .NET | Accettata |
| [0002](adr/0002-microservizi.md) | Architettura a microservizi | Accettata |
| [0003](adr/0003-messaggistica-nats.md) | NATS JetStream come bus di eventi | Accettata (2026-11-20, dopo lo scheletro del sistema) |
| [0004](adr/0004-database-postgres-timescale.md) | PostgreSQL con TimescaleDB, uno schema per servizio; EF Core + `COPY` binario | Accettata (2026-11-20, dopo lo scheletro del sistema) |
| [0005](adr/0005-frontend-react.md) | Frontend React + TypeScript + Vite | Accettata |
| [0006](adr/0006-contenitori-docker-podman.md) | Contenitori neutri tra Docker e Podman | Accettata |
| [0007](adr/0007-micro-frontend.md) | Micro-frontend: non adottati all'inizio | Proposta |
| [0008](adr/0008-frontend-responsivo-con-guscio-nativo.md) | Front-end responsivo con due layout, predisposto per un guscio nativo | Accettata |

## Decisioni aperte

- **Micro-frontend:** si parte con un'unica app modulare; la scelta va rivalutata quando la UI cresce (ADR 0007). Oggi c'è una sola feature (`activities`), quindi la scissione non è stata messa alla prova.
- **Autenticazione:** identity interno (ASP.NET Core Identity + OpenIddict) o provider esterno; vedi [06-security.md](06-security.md).
- **Libreria di grafici e mappe:** Recharts, visx o ECharts; MapLibre per le mappe. Da decidere dopo un prototipo.
- **Modello multi-utente:** solo atleti singoli all'inizio; chi pianifica può essere un allenatore oppure l'atleta stesso (vedi [04-frontend.md](04-frontend.md)): il ruolo allenatore cambia i confini di `identity` e `training-plan`.
- **Guscio nativo e store:** Capacitor dopo lo scheletro (T21); per l'iPhone servono l'account Apple Developer e una compilazione su macchine macOS nel cloud; da decidere insieme alla pipeline (ADR 0008).

### Chiuse dallo scheletro del sistema (2026-11-20)

La chiusura di queste due voci e il passaggio a *Accettata* degli ADR 0003 e 0004 sono previsti dal piano T20 (`plan.md`); nessun altro stato ADR è cambiato.

- **Accesso ai dati:** EF Core per tabelle e migrazioni, `COPY` binario e SQL parametrizzato con Npgsql per gli stream; Dapper non usato ([ADR 0004](adr/0004-database-postgres-timescale.md), DEC-003).
- **Bus di eventi:** NATS JetStream confermato dal prototipo ([ADR 0003](adr/0003-messaggistica-nats.md)); RabbitMQ non serve.

### Emerse dallo scheletro (aperte)

Limiti e scelte rimandate, tutti ancora veri nel codice al 2026-11-20 (fonti: registro di sviluppo in `agents/pflow/features/walking-skeleton-import/context.md` e README dei servizi):

- **Indice per il filtro `externalId`:** il filtro `GET /activities?externalId=` usa solo il prefisso `user_id` dell'indice univoco `(user_id, source, external_id)`; un indice su `(user_id, external_id)` richiede una migrazione approvata. Con più fonti lo stesso `externalId` potrebbe restituire più righe.
- **Ordine tra punti allo stesso istante:** oggi per valori delle colonne e non quello del file (DEC-018); una colonna `seq` richiederebbe una migrazione. Il dettaglio filtra `activity_id` senza vincolo su `time`: da rivedere con molti chunk (salvare numero di punti, colonne presenti e intervallo di tempo nell'attività).
- **Compressione, conservazione e aggregati continui** di TimescaleDB non sono configurati.
- **Obbligatorietà dei campi nei contratti:** un `sport` o numeri obbligatori mancanti in `ActivityImported` diventano `run` e 0 senza segnalazione (serve `[JsonRequired]` o `RespectRequiredConstructorParameters` in `ContractJson`).
- **Gateway:** la rotta verso `ingestion` è `/api/ingestion/{**remainder}` e quindi espone anche `/health/*` (e in `Development` `/openapi/v1.json`) di `ingestion`: restringerla a `/api/ingestion/uploads` è una scelta aperta; `AllowedHosts: "*"` in tutti i servizi (ferma il DNS rebinding se ristretto a `localhost;127.0.0.1`, ma non il CSRF); **CSRF** sull'upload in `Development` con identità fissa (`DisableAntiforgery()` esplicito, nessuna mitigazione oggi: servono un controllo di `Origin` e/o un token anti-falsificazione, oppure un'identità reale con token Bearer **[Ipotesi]**, vedi `06-security.md`); metodi HTTP non ristretti; nessuna prova con TLS o HTTP/2; il 413 di un file grande attraverso il gateway non è leggibile dal browser (il web controlla i 32 MiB prima di inviare).
- **Upload:** nessun limite di concorrenza (circa 3 volte la dimensione del file in memoria per richiesta: valutare `Upload:MaxConcurrent` con 503 prima di aprire il servizio a più utenti); nessun tetto di tempo complessivo sulla lettura del corpo; un parser che ignora il token continua in background dopo il 422.
- **Messaggistica:** nessuno strumento di reinvio dalla DLQ, nessuna pulizia dell'inbox, nessun parcheggio automatico nell'outbox dei messaggi che falliscono sempre; la correlazione (`correlationId` nei log del consumatore, contesto di traccia nella busta) non è propagata.
- **Errore durante il `COPY`** (lascia la transazione da annullare, lo fa il consumatore) e **valori NaN/∞** accettati da `StreamWriter` solo nelle colonne di valori (gli scarti di tempo non finiti sono rifiutati da `StreamWriter.TimeTicks`); `ActivityImportedValidation` li rifiuta prima, con invio dell'evento in DLQ.
- **Immagini non fissate:** `timescale/timescaledb:latest-pg16` e `nats:2` (DEC-013): una nuova versione può cambiare il comportamento dei test.
- **Podman** non provato (compose e script di sviluppo, ADR 0006); **migrazioni in produzione**: nessun processo dedicato di applicazione.
