# TriMetric *(nome provvisorio)*

Piattaforma web (desktop e mobile) che unisce gli allenamenti di **corsa, bici e nuoto** in un unico posto. Affianca le statistiche di Strava e aiuta ad analizzare, programmare e pianificare l'allenamento, sia per l'atleta amatore sia per quello evoluto o élite.

> **Stato:** fase iniziale. Questo repository contiene la documentazione di progetto e lo scheletro delle cartelle. Nessun codice applicativo è ancora stato scritto.
> Il punto di partenza funzionale è il PoC [LucaFerro01/TriMetric](https://github.com/LucaFerro01/TriMetric); lo stack viene invece riprogettato da zero.

---

## Obiettivi

- Raccogliere in un'unica base dati le attività provenienti da più fonti (Strava, Zepp/Amazfit, file FIT/GPX; Bryton passa da Strava).
- Offrire analisi che vanno oltre quelle di Strava: carico, forma fisica, stime fisiologiche, confronto tra pianificato e svolto.
- Permettere di **programmare** gli allenamenti (piani, template, allenamenti a step) e di seguirne l'esecuzione.
- Funzionare bene su desktop e su telefono (PWA installabile).
- Essere un terreno di apprendimento per **C#/.NET** e per un'**architettura a microservizi** reale.

### Non obiettivi (per ora)

- Sostituire Strava come rete sociale (feed, segmenti, kudos).
- Consulenza medica o consigli personalizzati di tipo clinico.
- Supporto multi-squadra o multi-allenatore (resta una possibile evoluzione, vedi [Roadmap](#roadmap)).

---

## Funzionalità

Derivate dal PoC e organizzate per area. La priorità è indicativa.

### 1. Sincronizzazione e import

| Funzione | Note dal PoC | Priorità |
|---|---|---|
| Login e collegamento con **Strava** (OAuth 2.0) | Flusso `/auth/strava` e callback | MVP |
| Ingestione attività in tempo reale via **webhook Strava** | Webhook, coda e worker asincrono | MVP |
| Import manuale di file **FIT** e **GPX** | Parsing lato server | MVP |
| Sincronizzazione **Zepp/Amazfit** | API non ufficiale, può cambiare senza preavviso | Dopo MVP |
| **Bryton** | Arriva indirettamente tramite Strava | MVP (gratis con Strava) |
| Altri produttori (Garmin, Coros, Wahoo, Polar) | Non presenti nel PoC | Idea |

### 2. Attività

- Elenco con filtri per sport, periodo e distanza.
- Dettaglio: mappa del percorso, grafici di passo/velocità, frequenza cardiaca, potenza, cadenza, altimetria, giri (lap).
- Tre sport trattati come cittadini di prima classe, con metriche specifiche:
  - **Corsa:** passo, ritmo, dislivello, cadenza.
  - **Bici:** potenza, velocità, cadenza, dislivello.
  - **Nuoto:** vasche, ritmo per 100 m, SWOLF.

### 3. Analisi e statistiche

Dal PoC:

- **VO2max**: andamento nel tempo.
- **FTP**: stima della soglia di potenza in bici.
- **TDEE**: dispendio energetico giornaliero.
- **Riepiloghi** settimanali e mensili.

Da aggiungere (da definire nel dettaglio, vedi [`ARCHITECTURE/08-analytics.md`](ARCHITECTURE/08-analytics.md)):

- Carico di allenamento (TSS e varianti per sport) e curva **CTL/ATL/TSB** (condizione, fatica, forma).
- Zone di frequenza cardiaca, potenza e ritmo, configurabili per atleta.
- Record personali e confronto tra periodi.
- Stato di forma e tendenze, con spiegazione di come è calcolato ogni numero.

### 4. Programmazione e pianificazione

Dal PoC (pagina *Scheduling*, tabelle `scheduled_workouts`, step e libreria template):

- **Libreria di template** di allenamenti riutilizzabili.
- **Allenamenti a step** (riscaldamento, ripetute, recupero, defaticamento) con target per ritmo, potenza o frequenza cardiaca.
- **Calendario** di allenamenti pianificati.
- **Matching** automatico tra allenamento pianificato e attività effettivamente svolta, con indicatore di aderenza.

Da aggiungere:

- Piani su più settimane con periodizzazione (base, costruzione, picco, scarico) e obiettivo di gara.
- Esportazione degli allenamenti verso orologi e ciclocomputer (formato FIT workout), se le integrazioni lo permettono.

### 5. Profilo e impostazioni

- Dati dell'atleta (peso, età, FC massima, FTP, soglie) con storico.
- Gestione dei collegamenti ai servizi esterni e delle autorizzazioni.
- Esportazione e cancellazione dei propri dati.

### Pagine previste nel frontend

Dashboard · Attività · Dettaglio attività · Analisi · Calendario e programmazione · Libreria allenamenti · Upload · Profilo · Collegamenti

---

## Architettura in breve

Dettagli e motivazioni in [`ARCHITECTURE/`](ARCHITECTURE/README.md).

```mermaid
flowchart LR
    Web["Web app React<br/>PWA"] --> GW["gateway<br/>(YARP)"]
    GW --> ID["identity"]
    GW --> ACT["activities"]
    GW --> ANA["analytics"]
    GW --> PLAN["training-plan"]
    GW --> ING["ingestion"]

    Strava[("Strava<br/>OAuth + webhook")] --> ING
    Zepp[("Zepp / Amazfit")] --> ING
    Files[/"File FIT / GPX"/] --> ING

    ING -- "ActivityImported" --> BUS{{"NATS JetStream"}}
    ACT <--> BUS
    ANA <--> BUS
    PLAN <--> BUS

    ID --- DB[("PostgreSQL<br/>+ TimescaleDB<br/>1 schema per servizio")]
    ACT --- DB
    ANA --- DB
    PLAN --- DB
    ING --- DB
```

| Servizio | Responsabilità |
|---|---|
| `gateway` | Punto d'ingresso unico, routing, autenticazione dei token, rate limiting (YARP). |
| `identity` | Utenti, sessioni, token, collegamenti OAuth con i servizi esterni. |
| `ingestion` | Connettori Strava/Zepp, webhook, parsing FIT/GPX, normalizzazione in un modello comune. |
| `activities` | Archivio delle attività e delle serie temporali (stream) di ciascuna. |
| `analytics` | Calcolo di metriche, carico, zone, stime (VO2max, FTP, TDEE) e riepiloghi. |
| `training-plan` | Template, allenamenti a step, calendario, piani e matching pianificato/svolto. |

### Stack

| Ambito | Scelta | Perché |
|---|---|---|
| Backend | **C# / .NET**, ASP.NET Core minimal API | Obiettivo di apprendimento, ottime prestazioni, ecosistema maturo |
| Gateway | **YARP** | Reverse proxy di Microsoft, si integra in .NET |
| Messaggistica | **NATS JetStream** | Leggero, semplice da gestire in locale, persistenza e riconsegna dei messaggi |
| Database | **PostgreSQL + TimescaleDB** | Dati relazionali e serie temporali in un solo motore |
| Accesso ai dati | **EF Core** (e Dapper dove serve) | Da confermare, vedi [`03-data.md`](ARCHITECTURE/03-data.md) |
| Frontend | **React + TypeScript + Vite**, PWA | Più componenti disponibili, ottimo supporto degli strumenti di IA |
| UI e grafici | shadcn/ui, Recharts (o visx), MapLibre | Da confermare in fase di prototipo |
| Dati lato client | TanStack Query + TanStack Router | Cache e routing tipizzati |
| Contenitori | **Docker o Podman** (`compose.yaml` neutro) | Nessuna funzione specifica di un solo runtime |
| Sviluppo locale | **.NET Aspire** | Avvio e osservazione di tutti i servizi in locale |
| Osservabilità | **OpenTelemetry** | Log, metriche e tracce distribuite |

I punti ancora aperti (ad esempio micro-frontend sì/no, scelta tra EF Core e Dapper) sono elencati in [`ARCHITECTURE/README.md`](ARCHITECTURE/README.md#decisioni-aperte).

---

## Struttura del repository

```text
.
├── README.md
├── ARCHITECTURE/            Scelte architetturali e decisioni (ADR)
│   ├── README.md            Indice e decisioni aperte
│   ├── 00-overview.md … 08-analytics.md
│   └── adr/                 Un file per decisione
├── src/
│   ├── services/            Microservizi C#
│   │   ├── gateway/
│   │   ├── identity/
│   │   ├── ingestion/
│   │   ├── activities/
│   │   ├── analytics/
│   │   └── training-plan/
│   ├── shared/
│   │   └── contracts/       Eventi e contratti condivisi (solo definizioni)
│   └── web/                 Frontend React (PWA)
├── deploy/
│   ├── compose.yaml         Infrastruttura locale (PostgreSQL/Timescale, NATS)
│   ├── .env.example         Variabili di esempio (copiare in .env)
│   └── postgres/init/       Schemi e utenti per servizio (primo avvio)
└── .gitignore
```

Regole di base:

- Un servizio **non legge mai** il database di un altro: dialoga solo tramite API o eventi.
- `src/shared/contracts` contiene soltanto definizioni (eventi, DTO), mai logica.
- Ogni servizio ha il proprio `README.md` con responsabilità, dati posseduti, API ed eventi.

---

## Avvio (previsto)

Non ancora disponibile. A regime:

```bash
# infrastruttura locale (con Docker o Podman)
docker compose -f deploy/compose.yaml up -d
# oppure
podman compose -f deploy/compose.yaml up -d
```

Prerequisiti previsti: .NET SDK (ultima LTS), Node.js LTS con pnpm, Docker o Podman.

---

## Rischi e punti di attenzione

- **Termini e limiti dell'API Strava:** limiti di richieste e regole sull'uso e sulla conservazione dei dati vanno verificati prima di aprire il servizio ad altri utenti.
- **API Zepp non ufficiale:** può rompersi o essere bloccata; va isolata dietro un connettore sostituibile.
- **Dati personali e sanitari:** frequenza cardiaca, peso e posizione GPS sono dati sensibili; servono cifratura dei token, minimizzazione e cancellazione su richiesta (GDPR).
- **Complessità dei microservizi:** per un solo sviluppatore il costo operativo è alto. È una scelta consapevole di apprendimento; vedi [`adr/0002`](ARCHITECTURE/adr/0002-microservizi.md) per come limitarlo.

---

## Roadmap

1. **Fondamenta:** scheletro dei servizi, gateway, identity, infrastruttura locale, CI.
2. **Ingestione:** collegamento Strava, webhook, import FIT/GPX, archivio attività.
3. **Analisi:** metriche base, zone, riepiloghi, CTL/ATL/TSB.
4. **Programmazione:** template, allenamenti a step, calendario, matching.
5. **Frontend completo:** dashboard, dettaglio attività, pianificazione, PWA offline.
6. **Evoluzioni:** Zepp e altri produttori, piani periodizzati, modalità allenatore.

---

## Licenza

Da definire.
