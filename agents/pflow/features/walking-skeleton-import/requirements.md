# Requirements: Scheletro del sistema e import di file GPX/FIT

## Goal

Avere un primo percorso funzionante end-to-end che dimostri l'architettura a microservizi: un utente carica un file GPX o FIT dal web, `ingestion` lo traduce nel modello comune e pubblica `ActivityImported` su NATS, `activities` lo salva, e il web lo mostra in elenco e in dettaglio passando dal `gateway`.

## Current state

Il repository contiene solo documentazione (`README.md`, `ARCHITECTURE/`), i README dei servizi in `src/` e `deploy/compose.yaml` con PostgreSQL/TimescaleDB e NATS JetStream (verificato funzionante con Docker). Non esiste codice .NET né frontend. Sulla macchina sono installati .NET SDK 9.0.321 e 11.0.100 (nessun SDK 10; runtime 8, 9, 10 e 11), Node 22 e Docker; `pnpm` non è installato.

## Target state

- Una solution .NET con i progetti dei servizi `gateway`, `ingestion`, `activities`, un progetto `ServiceDefaults` e il progetto `contracts`, che compila e ha test.
- Un file GPX o FIT caricato dal web compare, entro pochi secondi, nell'elenco delle attività e ha una pagina di dettaglio.
- La comunicazione tra `ingestion` e `activities` avviene solo tramite evento su NATS, con outbox e consumo idempotente.
- Tutto parte in locale con l'infrastruttura di `deploy/compose.yaml` e i servizi eseguiti dal terminale o dall'editor.

## Actors

- **Atleta** (all'inizio un solo utente di sviluppo): carica file e consulta le proprie attività.
- **Sviluppatore:** avvia infrastruttura e servizi in locale.

## In scope

- Preparazione del repository .NET: `global.json`, solution, impostazioni comuni di build, progetto `ServiceDefaults` (health check, OpenTelemetry, configurazione).
- Progetto `contracts` con la busta degli eventi e l'evento `ActivityImported`.
- `ingestion`: upload di file GPX e FIT, parsing e normalizzazione, deduplicazione, pubblicazione con outbox.
- `activities`: consumo idempotente, salvataggio di attività e stream, API di elenco e dettaglio.
- `gateway` (YARP): instradamento verso `ingestion` e `activities`.
- Identità di sviluppo fissa, con filtro per `userId` su ogni endpoint.
- `web`: app React con pagina di upload, elenco attività e dettaglio.
- Test automatici (unitari e di integrazione) e documentazione di avvio locale.

## Out of scope

- Collegamento a Strava (OAuth, webhook, storico) e a Zepp.
- Servizio `identity`, registrazione, login e token reali.
- Servizi `analytics` e `training-plan` e relativi eventi.
- Grafici, mappe, zone, metriche e qualsiasi analisi.
- Dockerfile e esecuzione dei servizi applicativi nei contenitori.
- PWA e modalità offline; notifiche.
- Pipeline di integrazione continua.
- Correzione, modifica o cancellazione manuale di attività.

## Functional and technical requirements

### Preparazione

- **REQ-001:** Il repository ha una solution .NET che compila con `dotnet build` e un `global.json` che fissa la versione dell'SDK.
- **REQ-002:** I servizi `gateway`, `ingestion`, `activities` hanno come target framework .NET 10 (LTS).
- **REQ-003:** Un progetto `ServiceDefaults` fornisce a ogni servizio endpoint `/health/live` e `/health/ready`, logging strutturato con `correlationId` e OpenTelemetry (tracce e metriche), senza logica di business.
- **REQ-004:** `src/shared/contracts` contiene solo definizioni: busta evento (`id`, `type`, `version`, `occurredAt`, `userId`, `correlationId`, `data`) ed evento `ActivityImported` versione 1.
- **REQ-005:** Un solo comando documentato avvia l'infrastruttura (`deploy/compose.yaml`) e uno script o comando documentato avvia i servizi in locale.

### Ingestion

- **REQ-006:** `ingestion` espone un endpoint di upload (`multipart/form-data`) che accetta file `.gpx` e `.fit` e risponde con un esito di accettazione contenente l'identificativo della sorgente.
- **REQ-007:** Il file viene tradotto in un modello comune con: sport (`run`, `ride`, `swim`, `other`), inizio in UTC, durata, distanza, dislivello positivo, riepiloghi disponibili (frequenza cardiaca media e massima, potenza media, cadenza media) e stream di punti (tempo, latitudine, longitudine, quota, frequenza cardiaca, potenza, cadenza, velocità) con i soli campi presenti nel file.
- **REQ-008:** Il parsing di GPX estrae almeno tempo, posizione e quota, più frequenza cardiaca e cadenza dalle estensioni quando presenti; il parsing di FIT estrae sport, record, giri e riepilogo di sessione dove presenti.
- **REQ-009:** Un file non valido, vuoto, di tipo non supportato o oltre il limite di dimensione viene rifiutato con un errore chiaro (codice 4xx e messaggio) senza pubblicare alcun evento.
- **REQ-010:** Il parsing ha un tempo massimo; il superamento produce un errore e nessun evento.
- **REQ-011:** Caricare due volte lo stesso file (stesso contenuto) per lo stesso utente non crea un secondo evento né una seconda attività: la chiave di deduplicazione è `fonte + hash del contenuto` per l'utente.
- **REQ-012:** `ingestion` registra il dato di deduplicazione e l'evento da pubblicare in una **outbox** nella stessa transazione del proprio database (schema `ingestion`); un processo in background la svuota verso NATS JetStream.
- **REQ-013:** Se NATS non è raggiungibile al momento dell'upload, il caricamento riesce comunque e l'evento viene pubblicato appena il bus torna disponibile, senza perdite né doppioni.

### Activities

- **REQ-014:** `activities` consuma `ActivityImported` da JetStream con consumatore durevole e conferma il messaggio solo dopo aver salvato.
- **REQ-015:** Il consumo è idempotente: lo stesso evento ricevuto più volte (stesso `id`) produce una sola attività e nessun errore, tramite una tabella `inbox` o vincolo di unicità nello schema `activities`.
- **REQ-016:** Gli stream sono salvati in una **hypertable** TimescaleDB nello schema `activities`; il riepilogo dell'attività in una tabella relazionale.
- **REQ-017:** Un messaggio che fallisce ripetutamente viene ritentato con attesa crescente e, superato il limite di tentativi, spostato in una coda dei messaggi falliti senza bloccare gli altri.
- **REQ-018:** `activities` espone `GET /activities` (elenco paginato, ordinato dalla più recente, con filtro per sport) e `GET /activities/{id}` (riepilogo e stream); lo stream restituito è ridotto a un massimo di punti configurabile (predefinito 1000) mantenendo il primo e l'ultimo punto.
- **REQ-019:** `activities` pubblica la descrizione OpenAPI delle proprie API.

### Gateway e identità di sviluppo

- **REQ-020:** Il `gateway` (YARP) instrada `/api/ingestion/...` verso `ingestion` e `/api/activities/...` verso `activities`; i servizi non sono raggiungibili dall'esterno se non tramite il gateway nella configurazione di riferimento.
- **REQ-021:** In ambiente di sviluppo un'identità fissa (un `userId` configurabile) viene fornita ai servizi in modo uniforme; l'identità **non** è mai letta dal corpo o dai parametri della richiesta; in ambiente non di sviluppo l'identità fissa è disattivata e le richieste senza identità sono rifiutate con 401.
- **REQ-022:** Ogni query di `ingestion` e `activities` è filtrata per `userId`; un utente non vede né può leggere attività di un altro.

### Isolamento dei dati

- **REQ-023:** Ogni servizio usa solo la propria stringa di connessione, con l'utente di database dedicato (`ingestion_svc`, `activities_svc`) e il proprio schema; nessun servizio legge lo schema di un altro.
- **REQ-024:** Le migrazioni dello schema di ciascun servizio sono nel servizio, applicate all'avvio in sviluppo, compatibili all'indietro.

### Web

- **REQ-025:** `src/web` è un'applicazione React con TypeScript e Vite, con `pnpm` come gestore di pacchetti, che dialoga solo con il `gateway`.
- **REQ-026:** La pagina di upload permette di scegliere un file `.gpx` o `.fit`, mostra l'avanzamento e l'esito, e segnala chiaramente gli errori di validazione.
- **REQ-027:** La pagina elenco mostra le attività (data, sport, distanza, durata, dislivello) con filtro per sport e paginazione, e si aggiorna quando una nuova attività è disponibile senza ricaricare la pagina, anche con ritardo di consegna dell'evento (stato "in elaborazione" finché non compare).
- **REQ-028:** La pagina di dettaglio mostra il riepilogo dell'attività e i valori di base disponibili; per sport o campi assenti nel file mostra "non disponibile" senza errori.
- **REQ-029:** I tipi del client API del web sono generati dalla descrizione OpenAPI di `activities` e dell'upload di `ingestion`, non scritti a mano.
- **REQ-030:** L'interfaccia è utilizzabile su schermo di telefono (layout responsivo, nessuno scorrimento orizzontale a 375 px di larghezza).

### Verifica

- **REQ-031:** Esistono test unitari per i parser GPX e FIT con file di esempio nel repository (almeno un file per formato, con risultato atteso verificato a mano) e per la deduplicazione.
- **REQ-032:** Esiste almeno un test di integrazione con contenitori reali (PostgreSQL/TimescaleDB e NATS) che copre upload → evento → salvataggio → lettura, inclusi il doppio caricamento e il doppio consumo dello stesso evento.
- **REQ-033:** Il `README.md` e un documento di avvio spiegano prerequisiti, avvio dell'infrastruttura, avvio dei servizi e del web, esecuzione dei test.
- **REQ-034:** Nei log non compaiono coordinate GPS, dati di frequenza cardiaca né contenuto dei file caricati.

### Dimensione degli eventi (aggiunti in pianificazione, DEC-012)

- **REQ-035:** Un file con più di 100.000 punti viene rifiutato da `ingestion` con errore 4xx e senza evento; un'attività con fino a 100.000 punti viene importata correttamente.
- **REQ-036:** `ActivityImported` porta i punti dello stream in formato a colonne (un array per campo, `null` dove il campo è assente), con valori arrotondati da `ingestion` (DEC-012) in modo che un'attività da 100.000 punti con tutte le colonne resti sotto 8 MB, e il server NATS di `deploy/compose.yaml` accetta messaggi fino a 8 MB.

## Acceptance scenarios

### AC-001: Caricamento di un file GPX valido
- **Given** infrastruttura e servizi avviati e l'identità di sviluppo attiva
- **When** carico dal web un file GPX valido di una corsa
- **Then** ricevo conferma di accettazione, e entro pochi secondi la corsa compare in cima all'elenco con data, distanza, durata e dislivello corretti rispetto al file

### AC-002: Caricamento di un file FIT valido con frequenza cardiaca
- **Given** servizi avviati
- **When** carico un file FIT di un'uscita in bici con frequenza cardiaca e potenza
- **Then** l'attività compare con sport `ride` e nel dettaglio sono presenti frequenza cardiaca e potenza medie

### AC-003: Stesso file caricato due volte
- **Given** un file già importato
- **When** lo carico di nuovo
- **Then** l'elenco contiene una sola attività e nessun secondo evento `ActivityImported` è pubblicato

### AC-004: File non valido
- **Given** servizi avviati
- **When** carico un file vuoto, con estensione `.txt`, un GPX non ben formato o un file oltre il limite di dimensione
- **Then** ricevo un errore 4xx con messaggio chiaro, il web lo mostra, e non compare alcuna attività né alcun evento

### AC-005: NATS non disponibile durante il caricamento
- **Given** NATS fermo
- **When** carico un file valido
- **Then** il caricamento è accettato e, riavviato NATS, l'attività compare in elenco senza doppioni

### AC-006: Evento consegnato più volte
- **Given** un `ActivityImported` già elaborato
- **When** lo stesso evento (stesso `id`) viene consegnato di nuovo ad `activities`
- **Then** non si crea una seconda attività e non si registra alcun errore

### AC-007: Isolamento tra utenti
- **Given** attività di due utenti diversi presenti nel database
- **When** il primo utente richiede elenco e dettaglio
- **Then** vede solo le proprie attività e richiedere l'id di un'attività dell'altro restituisce 404

### AC-008: Isolamento tra schemi
- **Given** l'utente di database `activities_svc`
- **When** tenta di leggere o scrivere nello schema `ingestion`
- **Then** il database rifiuta l'operazione con errore di permessi

### AC-009: Identità fuori dallo sviluppo
- **Given** un servizio avviato con ambiente non di sviluppo
- **When** arriva una richiesta senza identità valida
- **Then** la risposta è 401 e nessun dato è restituito

### AC-010: Elenco con molte attività
- **Given** più attività di sport diversi
- **When** filtro per sport e scorro le pagine
- **Then** vedo solo l'sport scelto, ordinato dalla più recente, senza duplicati tra pagine

### AC-011: Stream ridotto
- **Given** un'attività con molte migliaia di punti
- **When** chiedo il dettaglio con il limite predefinito
- **Then** ricevo al massimo 1000 punti, compresi primo e ultimo

### AC-012: Messaggio che fallisce sempre
- **Given** un evento non elaborabile
- **When** raggiunge il numero massimo di tentativi
- **Then** finisce nella coda dei messaggi falliti e gli eventi successivi continuano a essere elaborati

### AC-013: Avvio da zero
- **Given** un clone pulito del repository con Docker, .NET e Node installati
- **When** seguo il documento di avvio
- **Then** infrastruttura, servizi e web partono e posso eseguire AC-001

### AC-014: Attività molto lunga
- **Given** un file con circa 50.000 punti (circa 14 ore a 1 Hz), uno con esattamente 100.000 punti con tutti i campi valorizzati e uno con 100.001 punti
- **When** li carico tutti
- **Then** i primi due vengono importati e compaiono in elenco con lo stream completo nel database; il terzo è rifiutato con errore 4xx e non produce alcun evento

## Constraints

- Backend in C# su .NET (ADR 0001), architettura a microservizi (ADR 0002), bus NATS JetStream (ADR 0003, ancora da confermare con questo lavoro), PostgreSQL con TimescaleDB e schema per servizio (ADR 0004), frontend React + TypeScript + Vite (ADR 0005), contenitori compatibili con Docker e Podman (ADR 0006).
- Nessun codice condiviso tra servizi oltre a `src/shared/contracts`, `ServiceDefaults` e `Messaging` (DEC-001); questi ultimi contengono solo infrastruttura, nessuna logica di dominio, e non dipendono da alcun servizio.
- Un servizio non legge mai il database di un altro.
- Tutti gli istanti in UTC; unità interne SI (metri, secondi, watt, m/s).
- Nessun segreto nel repository; password di database valide solo per lo sviluppo.
- Migrazioni di schema soggette ad approvazione (`migrations: approval` nella configurazione PFlow).
- Commit manuali (`commit_policy: manual`), convenzione `conventional`.

## Edge cases

- GPX senza timestamp, senza quota o con più tracce/segmenti; GPX con punti non ordinati.
- FIT senza GPS (nuoto in piscina, rullo) o con più sessioni (multisport).
- File molto grandi (attività di molte ore) e file corrotti o troncati.
- Stesso file con nome diverso (la deduplicazione è sul contenuto).
- Orologi/dispositivi con fusi orari o orari non coerenti.
- Eventi fuori ordine o consegnati dopo un riavvio del servizio.
- Servizio `activities` fermo mentre `ingestion` pubblica: l'evento resta in JetStream.

## Assumptions

- **Target framework:** .NET 10 (LTS) come da ADR 0001 ("ultima LTS"). Sulla macchina è installato l'SDK 11.0.100 (nessun SDK 10) e i runtime 10 e 11: l'SDK 11 compila per `net10.0`. `global.json` fissa l'SDK installato (11.0.100); se si preferisce installare l'SDK 10, il target non cambia.
- **Limite di upload:** 32 MiB predefinito (era 25 MB: un GPX da 100.000 punti con tutte le colonne pesa circa 26,3 MiB, 29,3 MiB se indentato, e AC-014 lo richiede), configurabile fino a 64 MiB; tempo massimo di parsing 30 secondi, configurabile.
- **Identità di sviluppo:** attiva solo con ambiente `Development`; sarà sostituita da `identity` in una feature successiva senza cambiare i contratti degli endpoint.
- **Sport da FIT/GPX:** `run`, `ride`, `swim` mappati dal campo sport del FIT o dal tipo della traccia GPX; in assenza, `other`.
- **Ambiente di esecuzione:** i servizi girano sul PC dello sviluppatore con .NET SDK; solo database e bus sono in contenitori.
- **Libreria FIT:** scelta lasciata alla pianificazione (SDK FIT di Garmin per .NET o libreria di terze parti), con vincolo di licenza compatibile.

## Open questions

- Nessuna. Le scelte emerse in pianificazione (libreria FIT, contratti, aggiornamento della lista, accesso ai dati, dimensione degli eventi) sono registrate in `decisions.md`.
