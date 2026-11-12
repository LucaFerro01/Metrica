# 01 · Servizi

Ogni servizio è un progetto .NET indipendente, con proprio database (schema), propria immagine di contenitore e propria suite di test. Le API sono HTTP/JSON con descrizione OpenAPI; gli eventi sono descritti in [`src/shared/contracts`](../src/shared/contracts/README.md).

> I confini sono un'**[Ipotesi]** di partenza. Si verificano scrivendo i primi casi d'uso: se due servizi si chiamano continuamente per ogni richiesta, il confine è sbagliato.

## Riepilogo

| Servizio | Possiede | Pubblica | Consuma |
|---|---|---|---|
| `gateway` | Nulla (nessun dato) | — | — |
| `identity` | Utenti, credenziali, sessioni, collegamenti OAuth | `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` | — |
| `ingestion` | Stato di sincronizzazione, coda di lavoro, file grezzi caricati | `ActivityImported` | `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` |
| `activities` | Attività normalizzate, stream (serie temporali), giri | `ActivityStored`, `ActivityUpdated`, `ActivityDeleted` | `ActivityImported`, `UserDeleted` |
| `analytics` | Profilo fisiologico, zone, metriche derivate, riepiloghi | `MetricsUpdated`, `ThresholdsChanged` | `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`, `UserDeleted` |
| `training-plan` | Template, allenamenti a step, calendario, piani, esiti del matching | `WorkoutMatched`, `PlanChanged` | `ActivityStored`, `ThresholdsChanged`, `UserDeleted` |

## gateway

- Reverse proxy **YARP**: instrada `/api/<servizio>/...` verso il servizio corretto.
- Valida i token e passa l'identità dell'utente ai servizi a valle.
- Rate limiting e CORS.
- Espone l'endpoint pubblico per i webhook di Strava e lo inoltra a `ingestion`.
- Non contiene logica di business né database.

## identity

- Registrazione e accesso. Metodo principale: accesso con Strava; email e password come alternativa **[Ipotesi]**.
- Emette token di accesso brevi e token di refresh.
- Conserva, cifrati, i token OAuth dei servizi esterni collegati (Strava, Zepp).
- Gestisce esportazione e cancellazione dei dati dell'utente, coordinando gli altri servizi con `UserDeleted`.
- Dettagli in [06-security.md](06-security.md).

## ingestion

- **Connettori** isolati per fonte: `Strava`, `Zepp`, `FitGpx`. Ognuno implementa la stessa interfaccia e traduce nel modello comune.
- Riceve i webhook, risponde subito, accoda il lavoro e lo elabora in background (come il worker del PoC, ma nello stesso servizio come processo in background o come secondo contenitore).
- Gestisce i limiti di richieste dell'API di Strava con una coda a ritmo controllato e con ripetizione dei tentativi.
- Parsing di file FIT e GPX caricati dall'utente.
- Pubblica `ActivityImported` con attività e stream già normalizzati. Non sa nulla di analisi.
- Conserva una chiave di deduplicazione (`fonte + id esterno`) per l'idempotenza.

## activities

- Archivio canonico delle attività dell'utente.
- Modello: attività (sport, inizio, durata, distanza, dislivello, riepiloghi) e **stream** a campionamento regolare (tempo, posizione, quota, FC, potenza, cadenza, velocità) in tabelle Timescale.
- API di lettura: elenco con filtri, dettaglio, stream con riduzione dei punti (*downsampling*) per i grafici.
- Correzioni manuali (sport, titolo, esclusione dalle statistiche) pubblicano `ActivityUpdated`.

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

Struttura minima e uniforme **[Ipotesi]**:

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
