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

Ogni schema contiene anche le tabelle `outbox` e `inbox` (id dei messaggi già elaborati) per l'affidabilità descritta in [02-communication.md](02-communication.md).

## Serie temporali

Gli stream (un campione ogni 1–few secondi per tempo, posizione, FC, potenza, ecc.) sono la parte più voluminosa.

- **Hypertable** partizionata per tempo, con chiave per attività.
- **Compressione** nativa di Timescale sui dati più vecchi.
- **Downsampling per la lettura:** l'API restituisce al massimo circa N punti per grafico (es. algoritmo LTTB), così il frontend resta fluido anche con attività di ore.
- Aggregati continui (*continuous aggregates*) per riepiloghi di periodo, dove servono.

## Accesso ai dati in C#

**[Ipotesi]** **EF Core** come scelta predefinita (migrazioni, modello, produttività); **Dapper** o SQL diretto per i percorsi di lettura degli stream e le query analitiche, dove serve controllo sulle prestazioni. Da confermare con un prototipo; è una decisione aperta.

## Migrazioni

- Ogni servizio possiede e applica le proprie migrazioni.
- Le migrazioni sono **compatibili all'indietro** (si aggiunge, non si rimuove in un solo passaggio), così una versione nuova e una vecchia possono convivere durante un rilascio.
- In sviluppo si applicano all'avvio; in produzione come passo esplicito del rilascio **[Ipotesi]**.

## Identificatori e tempo

- Identificatori **UUID** (v7 dove disponibile, ordinabili nel tempo) generati dal servizio proprietario.
- Tutti gli istanti in **UTC**; il fuso orario dell'utente si applica solo in presentazione e nel calcolo dei "giorni" per i riepiloghi.
- Unità interne in **SI** (metri, secondi, watt, m/s); conversioni (km/h, min/km, miglia) solo in presentazione.

## Dati personali

Posizione GPS, frequenza cardiaca e peso sono dati sensibili. Regole in [06-security.md](06-security.md): cifratura dei token, cancellazione su `UserDeleted`, backup con la stessa politica di conservazione.

## Backup

**[Ipotesi]** Dump periodici di PostgreSQL con conservazione limitata; prova di ripristino periodica. I dettagli si definiscono con l'infrastruttura di produzione.
