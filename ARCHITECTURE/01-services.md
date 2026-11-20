# 01 · Servizi

Ogni servizio è un progetto .NET indipendente, con proprio schema di database (e ruolo dedicato) e propria suite di test. Le API sono HTTP/JSON con descrizione OpenAPI; gli eventi sono descritti in [`src/shared/contracts`](../src/shared/contracts/README.md). L'immagine di contenitore per servizio è prevista ma non esiste ancora (nessun Dockerfile): oggi i servizi girano dal terminale (vedi [05-infrastructure.md](05-infrastructure.md)).

> I confini sono un'**[Ipotesi]** di partenza. Si verificano scrivendo i primi casi d'uso: se due servizi si chiamano continuamente per ogni richiesta, il confine è sbagliato. Lo scheletro del sistema (import di file GPX/FIT) ha messo alla prova solo `ingestion` → `activities` (un evento, nessuna chiamata sincrona tra i due), senza ragioni per spostare il confine.

## Riepilogo

La colonna *Stato* dice che cosa esiste nel repository (vocabolario *Implementato*, *Previsto*, **[Ipotesi]** in [README](README.md#come-si-usa-questa-cartella)); le altre descrivono il disegno completo (ciò che non è implementato resta **previsto**).

| Servizio | Stato | Possiede | Pubblica | Consuma |
|---|---|---|---|---|
| `gateway` | **Implementato** nello scheletro (YARP; instradamento di `ingestion` e `activities`) | Nulla (nessun dato) | — | — |
| `identity` | Previsto (solo README) | Utenti, credenziali, sessioni, collegamenti OAuth | `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` | — |
| `ingestion` | **Implementato in parte**: upload e parsing di file GPX/FIT; mancano connettori Strava e Zepp, webhook, coda di lavoro per fonte | Registro dei caricamenti (`uploads`, deduplicazione per contenuto), `outbox`; i file grezzi **non** sono conservati | `ActivityImported` (implementato) | `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` (previsti, non implementati) |
| `activities` | **Implementato in parte**: consumo di `ActivityImported`, API di lettura; mancano correzioni manuali e pubblicazione di eventi | Attività, giri e stream (hypertable) in `activities`, `inbox` | `ActivityStored`, `ActivityUpdated`, `ActivityDeleted` (previsti, non implementati) | `ActivityImported` (implementato); `UserDeleted` (previsto) |
| `analytics` | Previsto (solo README) | Profilo fisiologico, zone, metriche derivate, riepiloghi | `MetricsUpdated`, `ThresholdsChanged` | `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`, `UserDeleted` |
| `training-plan` | Previsto (solo README) | Template, allenamenti a step, calendario, piani, esiti del matching | `WorkoutMatched`, `PlanChanged` | `ActivityStored`, `ThresholdsChanged`, `UserDeleted` |

## Progetti nel repository

Ogni servizio è **un solo progetto** `TriMetric.<Servizio>` in `src/services/<servizio>/src/`, con i test in `src/services/<servizio>/tests/TriMetric.<Servizio>.Tests/` (DEC-004). Tutti hanno target `net10.0` e stanno nella solution `TriMetric.slnx` alla radice, con versioni dei pacchetti centralizzate in `Directory.Packages.props`. I servizi `identity`, `analytics` e `training-plan` hanno per ora solo la cartella con il README (nessun progetto .NET né migrazione); lo script di init del database ne crea già ruolo e schema e `EVENTS` ne riserva il prefisso dei subject.

Progetti **condivisi** (DEC-001, DEC-004), gli unici codici che più servizi possono usare:

| Progetto | Cartella | Contenuto | Dipende da |
|---|---|---|---|
| `TriMetric.Contracts` | `src/shared/contracts/` | Solo definizioni: busta `EventEnvelope<T>`, `ActivityImported` (v1), `StreamColumns`, `Sport`, nomi di eventi, subject e stream (`EventNames`), `StreamLimits`, `ContractJson.Options` | niente |
| `TriMetric.ServiceDefaults` | `src/shared/ServiceDefaults/` | `/health/live` e `/health/ready`, logging JSON con `correlationId`, OpenTelemetry (esportazione OTLP solo con un endpoint configurato), identità di sviluppo `AddDevelopmentIdentity` e `GetUserId` rigido | niente di interno |
| `TriMetric.Messaging` | `src/shared/Messaging/` | Solo infrastruttura (nessuna logica di dominio): client e stream NATS, outbox con relay, inbox e consumatore con retry e DLQ (vedi [02-communication.md](02-communication.md)) | `contracts` (nessuna dipendenza da un servizio né da `ServiceDefaults`) |

`gateway` dipende solo da `ServiceDefaults` (e da `Yarp.ReverseProxy`: nessun database, nessun NATS); `ingestion` e `activities` da tutti e tre. Test condivisi in `tests/`: `TriMetric.TestInfrastructure` (fixture Testcontainers per TimescaleDB e NATS, DEC-013), i suoi test e `TriMetric.IntegrationTests` (i tre servizi veri in processo con contenitori reali).

## gateway

Stato: **implementato** (YARP 2.3.0, [README](../src/services/gateway/README.md)).

- Reverse proxy **YARP**: instrada `/api/ingestion/{...}` verso `ingestion` e `/api/activities/{...}` verso `activities` (una rotta per servizio, ogni altro percorso è 404 dal gateway).
- Confine di sicurezza sull'identità: toglie le intestazioni di identità (`X-User-Id` e simili) e il parametro `userId` dal client; non aggiunge mai identità (REQ-021).
- Limite del corpo delle richieste (32 MiB più 1 MiB di overhead multipart) con corpo in streaming.
- **Previsti, non implementati:** validazione dei token e passaggio dell'identità dell'utente ai servizi a valle, rate limiting, CORS (necessario per il guscio nativo, ADR 0008), endpoint pubblico per i webhook di Strava inoltrato a `ingestion`.
- Non contiene logica di business né database.

## identity

- Registrazione e accesso. Metodo principale: accesso con Strava; email e password come alternativa **[Ipotesi]**.
- Emette token di accesso brevi e token di refresh.
- Conserva, cifrati, i token OAuth dei servizi esterni collegati (Strava, Zepp).
- Gestisce esportazione e cancellazione dei dati dell'utente, coordinando gli altri servizi con `UserDeleted`.
- Dettagli in [06-security.md](06-security.md).

## ingestion

Stato: **implementato in parte** ([README](../src/services/ingestion/README.md)).

- **Connettori** isolati per fonte: `Strava`, `Zepp`, `FitGpx`. Ognuno implementa la stessa interfaccia e traduce nel modello comune. *Oggi esiste solo il caso dei file:* `IActivityFileParser` con `GpxParser` e `FitParser` (DEC-002, `Garmin.FIT.Sdk` per il FIT); Strava e Zepp sono previsti.
- Riceve i webhook, risponde subito, accoda il lavoro e lo elabora in background (come il worker del PoC, ma nello stesso servizio come processo in background o come secondo contenitore). *Previsto, non implementato.* Realizzato: `POST /uploads` (multipart, un file `.gpx`/`.fit`, limite 32 MiB, parsing con tempo massimo, tetto di 100.000 punti), che risponde 202 e lascia la pubblicazione al relay dell'outbox.
- Gestisce i limiti di richieste dell'API di Strava con una coda a ritmo controllato e con ripetizione dei tentativi. *Previsto.*
- Parsing di file FIT e GPX caricati dall'utente. *Implementato*, con i file letti in streaming e tenuti in memoria (circa 3 volte la dimensione per richiesta, senza limite di concorrenza: limite noto del README del servizio).
- Pubblica `ActivityImported` con attività e stream già normalizzati (stream a colonne arrotondato, DEC-012). Non sa nulla di analisi. *Implementato* con outbox e relay.
- Conserva una chiave di deduplicazione per l'idempotenza: nello scheletro `(utente, fonte, id esterno)` con fonte `file` e id esterno uguale allo SHA-256 del contenuto (REQ-011); `uploads` registra anche nome del file e dimensione, mai il contenuto.

## activities

Stato: **implementato in parte** ([README](../src/services/activities/README.md)).

- Archivio canonico delle attività dell'utente.
- Modello: attività (sport, inizio, durata, distanza, dislivello, riepiloghi), giri e **stream** (tempo, posizione, quota, FC, potenza, cadenza, velocità) nella hypertable `activity_stream`. Lo stream arriva già campionato dal file (nessun ricampionamento a passo regolare).
- Consuma `ActivityImported` con un consumatore durevole (`activities-activity-imported`), inbox e `INSERT ... ON CONFLICT DO NOTHING` sulla chiave di deduplicazione: lo stesso evento consegnato due volte produce una sola attività (REQ-014, REQ-015).
- API di lettura *(implementata)*: `GET /activities` (elenco paginato, filtri `sport` e `externalId`) e `GET /activities/{id}?maxPoints=` (dettaglio con giri e stream ridotto, DEC-017, DEC-018), con OpenAPI; ogni query è filtrata per l'utente del principal.
- Correzioni manuali (sport, titolo, esclusione dalle statistiche) pubblicano `ActivityUpdated`. *Previsto, non implementato*; non sono implementati nemmeno `ActivityStored` e `ActivityDeleted` né il consumo di `UserDeleted`.

## analytics

- Profilo fisiologico dell'atleta con storico: FC massima, FTP, soglie, peso.
- Calcola per ogni attività carico (TSS e varianti), tempo nelle zone e record, e a livello atleta CTL/ATL/TSB, VO2max stimato, FTP stimato, TDEE.
- Mantiene i riepiloghi settimanali e mensili.
- Si basa su una **copia locale** dei dati di attività strettamente necessari (riassunti e stream ridotti) per non chiamare `activities` a ogni calcolo; vedi [02-communication.md](02-communication.md).
- Formule e ipotesi in [08-analytics.md](08-analytics.md).

## training-plan

- **Template** di allenamento riutilizzabili e **allenamenti a step** con target (ritmo, potenza, FC, RPE).
- **Calendario** degli allenamenti pianificati e, in seguito, **piani** a più settimane.
- **Matching:** associa un'attività svolta a un allenamento pianificato (stesso giorno, sport e durata/distanza compatibili) e calcola l'aderenza. Algoritmo portato dal PoC (`matching.ts`), riscritto in C# lato server per poterlo testare e riusare.
- Usa le soglie dell'atleta (da `ThresholdsChanged`) per tradurre target relativi (es. "90% FTP") in valori assoluti.

## Struttura interna di un servizio

Struttura minima e uniforme **[Ipotesi]**. Nello scheletro i tre servizi implementati sono **un solo progetto ciascuno** (DEC-004, vedi *Progetti nel repository*): la divisione in quattro progetti non è stata adottata perché la logica di dominio è ancora poca. Il disegno seguente resta l'obiettivo per quando ce ne sarà abbastanza:

```text
<servizio>/
├── src/
│   ├── <Servizio>.Api/           Endpoint, configurazione, composizione
│   ├── <Servizio>.Application/   Casi d'uso, comandi, query
│   ├── <Servizio>.Domain/        Entità e regole (senza dipendenze esterne)
│   └── <Servizio>.Infrastructure/ Database, messaggistica, client esterni
├── tests/
│   ├── <Servizio>.UnitTests/
│   └── <Servizio>.IntegrationTests/
├── Dockerfile
└── README.md
```

Servizi semplici (`gateway`, `identity`) possono restare in un unico progetto; la divisione in quattro progetti si adotta dove c'è logica di dominio vera (`ingestion`, `activities`, `analytics`, `training-plan`).
