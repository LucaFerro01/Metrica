# ADR 0004 · PostgreSQL con TimescaleDB, uno schema per servizio

- **Stato:** Accettata (2026-11-20, confermata dallo scheletro del sistema: vedi *Esito dello scheletro del sistema*)
- **Data:** 2026-11-12

## Contesto

Servono dati relazionali (utenti, template, calendario) e grandi quantità di serie temporali (stream delle attività). Il PoC usava già PostgreSQL con TimescaleDB e Drizzle.

## Decisione

- **PostgreSQL** con estensione **TimescaleDB** per le serie temporali.
- **Un'istanza** in sviluppo, **uno schema per servizio** con utente dedicato e permessi solo sul proprio schema.
- Ogni servizio gestisce le proprie migrazioni.
- Accesso ai dati: **EF Core** per impostazione predefinita; Dapper/SQL diretto dove serve controllo sulle prestazioni (stream, analisi). *Decisione aperta* al momento della proposta; chiusa dallo scheletro: vedi *Esito dello scheletro del sistema*.

## Motivazioni

- Un solo motore per due tipi di dato riduce la complessità operativa.
- Isolamento imposto dal database: un servizio non può leggere lo schema di un altro.
- Timescale offre compressione e aggregati continui, utili per stream e riepiloghi.
- Si può passare a istanze separate per servizio cambiando solo la stringa di connessione.

## Conseguenze

- Istanza unica = un solo punto di guasto in produzione; accettabile per questa fase.
- Servizi che condividono l'istanza possono influenzarsi nelle prestazioni (carico dell'analisi sull'archivio).
- Necessario curare permessi e script di inizializzazione per rendere reale l'isolamento.

## Alternative considerate

- **Un database per servizio fin dall'inizio:** isolamento massimo, ma più peso in locale. Possibile in seguito.
- **Un database specializzato per le serie temporali (InfluxDB, ClickHouse):** più motori da gestire; non necessario per questi volumi.
- **Database per tipo (documentale per gli stream):** meno adatto alle query sugli intervalli e ai riepiloghi.

## Esito dello scheletro del sistema (2026-11-20)

Sezione aggiornata con l'esito dello scheletro, come previsto dal piano T20 (verifica finale e aggiornamento dell'architettura). La chiusura della questione sull'accesso ai dati e il passaggio a *Accettata* sono quelli previsti dal piano T20; nessun altro stato è cambiato. Le misure di tempo e di dimensione riportate sotto vengono dal registro di sviluppo dello scheletro (`agents/pflow/features/walking-skeleton-import/context.md`, T11 e T13), non sono ricavate dal codice.

La decisione è confermata dallo scheletro (`ingestion` e `activities` su un'unica istanza TimescaleDB, uno schema e un ruolo ciascuno). La questione aperta sull'accesso ai dati è **chiusa**: EF Core ovunque per tabelle, indici, outbox/inbox e migrazioni (DEC-003), più due percorsi di SQL diretto con Npgsql nello stesso servizio `activities`, sulla connessione e (in scrittura) sulla transazione del `DbContext`: `COPY` binario per scrivere gli stream (`StreamWriter`, 100.000 punti circa 0,8–1,9 s misurati in T11) e SQL parametrizzato per leggerli ridotti (`ActivityStreamReader`). Dapper non è usato. Fatti verificati nelle migrazioni, nei `DbContext` e nello script `deploy/postgres/init/01-schemas.sql`:

- **Uno schema e un ruolo per servizio**: lo script crea `<servizio>_svc` con login e lo schema di sua proprietà per `identity`, `ingestion`, `activities`, `analytics`, `training_plan`; `public` resta senza `CREATE` e senza accesso per `PUBLIC`. Un ruolo non legge né scrive lo schema di un altro, nemmeno i chunk della hypertable (errore `42501`, AC-008 in `SchemaIsolationTests`).
- **Permessi per le hypertable** (DEC-014): l'estensione `timescaledb` risiede in `public`, quindi ogni ruolo riceve `USAGE` su `public` e `search_path = <schema>, public`; senza questo `create_hypertable` non si trova o dà `permission denied`. Lo script gira solo alla prima creazione del volume: dopo ogni modifica serve `docker compose -f deploy/compose.yaml down -v` (perde i dati di sviluppo).
- **Nomi `snake_case`** (DEC-003) con `EFCore.NamingConventions` 10.0.1; schema predefinito del servizio e tabella di storia `__EFMigrationsHistory` nello schema del servizio; ogni `DbContext` ha una factory di progettazione con stringa fissa senza segreti. Le migrazioni si applicano all'avvio solo in `Development` (REQ-024); altrove l'avvio non tocca lo schema.
- **Hypertable `activities.activity_stream`** (`activity_id`, `time` e sette colonne di valori `double precision` nulle): partizionata sulla sola colonna `time` con `SELECT create_hypertable('activities.activity_stream', 'time')` scritto a mano nella migrazione (EF non lo genera: va riaggiunto se la migrazione si rigenera), con le impostazioni predefinite di Timescale. **Nessuna chiave primaria** (una chiave che escluda `time` non è ammessa): indice non univoco `ix_activity_stream_activity_time` su `(activity_id, time)`; `create_hypertable` crea anche `activity_stream_time_idx` su `time` decrescente, ridondante ma tenuto (misurato: velocità del `COPY` invariata, circa 4 MB di indici su 11 MB per 100.000 punti). Compressione, politiche di conservazione e aggregati continui **non sono configurati**.
- **Tabelle relazionali di `activities`**: `activities` (indice univoco `ux_activities_user_source_external` su `(user_id, source, external_id)` per la deduplicazione, indice `ix_activities_user_start` su `(user_id, start_time DESC)`), `activity_laps` (chiave esterna con cancellazione a cascata) e `inbox` (chiave primaria `(consumer, message_id)`). `ingestion` ha `uploads` (stesso indice univoco `ux_uploads_user_source_external`) e `outbox` (indice parziale `ix_outbox_unpublished`).
- **Filtro `externalId`** (DEC-017): uguaglianza esatta parametrizzata tra le attività dell'utente, **senza indice dedicato**: usa il prefisso `user_id` dell'indice univoco e filtra `external_id` sulle righe dell'utente. Basta per lo scheletro; un indice su `(user_id, external_id)` richiederebbe una migrazione approvata (aperto, vedi `ARCHITECTURE/README.md`).
- **Riduzione in lettura** (`maxPoints`): la riduzione avviene nel database con `row_number()` e indici a passo costante (primo e ultimo punto sempre inclusi), non con LTTB; dal database escono al massimo `maxPoints` righe (predefinito 1000, minimo 2; il massimo accettato per richiesta è `Activities:MaxStreamPoints`, predefinito 5000 e configurabile da 1000 a 100.000). Il dettaglio con 100.000 punti e 8 colonne costa circa 0,2 s (T13).
- **Ordine tra punti allo stesso istante** (DEC-018): `ORDER BY time` seguito dai valori delle colonne (`ASC NULLS LAST`), non più `ctid`, perché il `ctid` non garantisce l'ordine di scrittura (causa di un test intermittente). L'ordine tra pari istante non è quello del file; una colonna `seq` richiederebbe una migrazione.
- **Idempotenza nel database**: l'`INSERT ... ON CONFLICT (user_id, source, external_id) DO NOTHING` e l'inbox rendono sicuro un evento consegnato più volte, anche con due transazioni concorrenti (provato in T12).

Limiti noti: `WHERE activity_id = @id` senza vincolo su `time` visita tutti i chunk e le statistiche rileggono le righe (accettabile ora, da rivedere con molti chunk); `StreamWriter` accetta valori NaN/∞ solo nelle colonne di valori (gli scarti di tempo non finiti sono rifiutati da `StreamWriter.TimeTicks`); `ActivityImportedValidation` li rifiuta prima, con invio dell'evento in DLQ; un errore durante il `COPY` lascia la transazione da annullare (lo fa il consumatore con il rollback); l'immagine `timescale/timescaledb:latest-pg16` non è fissata (DEC-013) e il testo esatto dell'indice creato da Timescale, asserito da un test, può cambiare con l'immagine. L'avvio reale contro lo stack di `deploy/compose.yaml` è provato dagli script e dal test `real-stack` del web (T17–T19); il carico con più istanze dello stesso servizio no.
