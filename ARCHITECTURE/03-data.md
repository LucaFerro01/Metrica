# 03 · Dati

## Scelta di base

**PostgreSQL con l'estensione TimescaleDB**, un'unica istanza in sviluppo con **uno schema per servizio** (ADR [0004](adr/0004-database-postgres-timescale.md)).

- Dati relazionali (utenti, template, calendario) e serie temporali (stream delle attività) nello stesso motore.
- Ogni servizio ha un **proprio utente di database** con permessi solo sul proprio schema: l'isolamento è imposto dal database, non solo dalla disciplina.
- In produzione lo stesso schema logico può restare su una sola istanza o essere spostato su istanze separate senza cambiare il codice: ogni servizio usa la propria stringa di connessione.

## Cosa va dove

| Servizio | Schema | Tipo di dati principali |
|---|---|---|
| `identity` | `identity` | Utenti, sessioni, collegamenti OAuth (token cifrati) |
| `ingestion` | `ingestion` | Stato di sincronizzazione, deduplicazione, coda di lavoro, file grezzi |
| `activities` | `activities` | Attività (relazionale), **stream (hypertable Timescale)**, giri |
| `analytics` | `analytics` | Profilo atleta con storico, metriche per attività, carico giornaliero, riepiloghi |
| `training-plan` | `training_plan` | Template, allenamenti, step, calendario, piani, esiti del matching |

Il servizio che **pubblica** eventi ha la tabella `outbox` e quello che **consuma** ha la tabella `inbox` (id dei messaggi già elaborati) per l'affidabilità descritta in [02-communication.md](02-communication.md): nello scheletro `ingestion` ha `outbox` e `activities` ha `inbox`. Lo schema reale dello scheletro (tabelle, indici, ruoli, permessi per le hypertable) è nell'[ADR 0004](adr/0004-database-postgres-timescale.md).

## Serie temporali

Gli stream (un campione ogni 1–few secondi per tempo, posizione, FC, potenza, ecc.) sono la parte più voluminosa.

- **Hypertable** `activities.activity_stream`, partizionata sulla colonna `time` (impostazioni predefinite di Timescale), **senza chiave primaria** (una chiave deve includere `time`: si usa l'indice non univoco `(activity_id, time)`); i punti si scrivono con `COPY` binario nella transazione del consumatore. *Realizzato nello scheletro.*
- **Compressione** nativa di Timescale sui dati più vecchi **[Ipotesi]**: non è configurata.
- **Downsampling per la lettura** *(realizzato)*: `GET /activities/{id}?maxPoints=` restituisce al massimo `maxPoints` punti (predefinito 1000, minimo 2, massimo `Activities:MaxStreamPoints`, predefinito 5000), sempre con il primo e l'ultimo, scelti **nel database** a passo costante con `row_number()` (dal database escono al massimo `maxPoints` righe). **Non** è un algoritmo come LTTB: LTTB resta un'**[Ipotesi]** da valutare quando arriveranno i grafici (DEC-018 e ADR 0004 per i dettagli).
- **Ordine tra punti allo stesso istante** (DEC-018): per `time` e, a parità di istante, per i valori delle colonne (`ASC NULLS LAST`); non è l'ordine del file e non dipende dal layout fisico (non si usa `ctid`). Se in futuro l'ordine di scrittura dovesse contare serve una colonna `seq` con una migrazione.
- **Filtro `externalId`** (DEC-017): uguaglianza esatta tra le attività dell'utente, senza un indice dedicato oltre all'indice univoco `(user_id, source, external_id)`.
- Aggregati continui (*continuous aggregates*) per riepiloghi di periodo, dove servono **[Ipotesi]**: non esistono.

## Accesso ai dati in C#

**Deciso** ([ADR 0004](adr/0004-database-postgres-timescale.md), DEC-003): **EF Core** per tabelle, indici, outbox/inbox e migrazioni; **SQL diretto con Npgsql** (stesso servizio, stessa connessione del `DbContext`) per i due percorsi in cui serve controllo: `COPY` binario per scrivere gli stream e query parametrizzata per leggerli ridotti. Dapper non è usato. Nomi di tabelle e colonne in `snake_case` (`EFCore.NamingConventions`). **[Ipotesi]** `analytics` (non ancora implementato) seguirà la stessa regola, da rivedere se emergono query che EF non esprime bene.

## Migrazioni

- Ogni servizio possiede e applica le proprie migrazioni.
- Le migrazioni sono **compatibili all'indietro** (si aggiunge, non si rimuove in un solo passaggio), così una versione nuova e una vecchia possono convivere durante un rilascio.
- In sviluppo si applicano all'avvio (solo con ambiente `Development`; altrove l'avvio non tocca lo schema); in produzione come passo esplicito del rilascio **[Ipotesi]**: non esiste ancora un processo dedicato.
- La migrazione dell'hypertable contiene un passo SQL scritto a mano (`create_hypertable`) che EF non genera: va riaggiunto se la migrazione si rigenera. Lo script di inizializzazione di PostgreSQL (`deploy/postgres/init/01-schemas.sql`) gira solo alla prima creazione del volume: dopo una modifica serve `down -v` (vedi [05-infrastructure.md](05-infrastructure.md)).

## Identificatori e tempo

- Identificatori **UUID** generati dal servizio proprietario. **[Ipotesi]** v7 dove disponibile, ordinabili nel tempo: nello scheletro solo l'id di `uploads` è v7 (`Guid.CreateVersion7()`); l'id della busta e quello delle attività sono `Guid.NewGuid()` (v4).
- Tutti gli istanti in **UTC**; il fuso orario dell'utente si applica solo in presentazione e nel calcolo dei "giorni" per i riepiloghi.
- Unità interne in **SI** (metri, secondi, watt, m/s); conversioni (km/h, min/km, miglia) solo in presentazione.

## Dati personali

Posizione GPS, frequenza cardiaca e peso sono dati sensibili. Regole in [06-security.md](06-security.md): cifratura dei token, cancellazione su `UserDeleted`, backup con la stessa politica di conservazione.

## Backup

**[Ipotesi]** Dump periodici di PostgreSQL con conservazione limitata; prova di ripristino periodica. I dettagli si definiscono con l'infrastruttura di produzione.
