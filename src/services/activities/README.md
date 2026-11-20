# activities

Archivio canonico delle attività e delle loro serie temporali.

- **Responsabilità:** salvataggio di attività, stream e giri; API di lettura (elenco con filtri, dettaglio, stream ridotti per i grafici); correzioni manuali.
- **Dati posseduti:** schema `activities` (ruolo `activities_svc`): `activities` (indice univoco `(user_id, source, external_id)` per la deduplicazione), `activity_laps`, `activity_stream` (hypertable Timescale senza chiave primaria, indice `(activity_id, time)`) e `inbox`. Dettagli e motivazioni in [ADR 0004](../../../ARCHITECTURE/adr/0004-database-postgres-timescale.md).
- **Pubblica (previsto, non implementato):** `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`.
- **Consuma:** `ActivityImported` (**implementato**, consumatore durevole `activities-activity-imported` sul subject `ingestion.activity.imported`); `UserDeleted` (previsto, non implementato).
- **Origine nel PoC:** `activities.routes.ts`, tabelle attività in `schema.ts`.
- **Stato (scheletro walking-skeleton-import):** implementati l'importazione di `ActivityImported` (consumatore con inbox e scrittura di attività, giri e stream) e le API di lettura (elenco con filtri, dettaglio con stream ridotto e filtro `externalId`, DEC-017). Non ancora implementati: la pubblicazione di `ActivityStored`, `ActivityUpdated` e `ActivityDeleted`, il consumo di `UserDeleted` e le correzioni manuali.
- **Host:** `dotnet run --project src/services/activities/src/TriMetric.Activities` (il profilo di avvio imposta `ASPNETCORE_ENVIRONMENT=Development`); Kestrel solo su `http://127.0.0.1:5082`, raggiungibile dal web attraverso il gateway (`/api/activities`). Migrazioni e `/openapi/v1.json` solo in `Development`; `/health/ready` controlla solo il database. Fuori da `Development` ogni richiesta protetta risponde 401.

Vedi [`03-data.md`](../../../ARCHITECTURE/03-data.md).

## Consumo di `ActivityImported`

`ActivityImportedHandler` valida l'evento **prima** di toccare il database (`ActivityImportedValidation`: versione e tipo della busta, campi obbligatori, numeri finiti, `Sport` definito, al massimo 100.000 punti, colonne di uguale lunghezza): un evento non valido va direttamente in DLQ (`dlq.activities-activity-imported`), senza ritentativi. Poi, nella transazione del consumatore, `INSERT ... ON CONFLICT (user_id, source, external_id) DO NOTHING` e, solo se la riga è nuova, giri e punti (`StreamWriter`, `COPY` binario); l'`user_id` viene solo dalla busta. Lo stesso evento consegnato due volte produce una sola attività. Opzioni (sezione `Consumers:ActivityImported`: `MaxDeliver`, `AckWaitSeconds`, `BackoffMilliseconds:N`; predefiniti 5, 60 s e 1, 2, 5, 10 s) validate all'avvio; concorrenza 1. Limiti noti: uno `sport` o numeri obbligatori mancanti nell'evento non sono distinguibili da un valore predefinito (`run`, 0); uno stream con 0 punti è accettato (il dettaglio lo gestisce); un messaggio che fallisce di continuo occupa lo slot del consumatore per circa 18 s prima della DLQ. Il comportamento di retry, inbox e DLQ è descritto nel [README di `Messaging`](../../shared/Messaging/README.md).

## API di lettura

Tutti gli endpoint richiedono l'autenticazione; l'utente viene solo dal principal (mai da parametri o intestazioni). Un'attività di un altro utente risponde 404 come un id inesistente.

- `GET /activities` — elenco paginato dell'utente, dalla più recente (`startTime` decrescente, poi `id`). Risposta `{ items, page, pageSize, total }`.
- `GET /activities/{id}?maxPoints=` — dettaglio con giri e stream ridotto.

Parametri di query di `GET /activities`, tutti opzionali e validati a mano (valore non valido, vuoto o ripetuto = 400 `ProblemDetails` `urn:trimetric:problem:invalid-query`, mai una correzione silenziosa):

| Parametro | Regola | Predefinito |
| --- | --- | --- |
| `sport` | uno tra `run`, `ride`, `swim`, `other` (minuscolo) | nessun filtro |
| `page` | intero ≥ 1 | 1 |
| `pageSize` | intero tra 1 e 100 | 20 |
| `externalId` | testo, corrispondenza **esatta** e sensibile alle maiuscole (uguaglianza con parametro SQL, mai `LIKE`: `%` e `_` non sono jolly); da 1 a 128 caratteri, senza spazi né caratteri di controllo, una sola volta | nessun filtro |

I filtri si combinano e `total` conta le attività dell'utente che li soddisfano tutti. Nessuna corrispondenza: 200 con `items` vuoto e `total` 0 (non 404). Il filtro `externalId` serve al web per riconoscere in modo esatto un file appena importato (DEC-017); l'unicità è per `(utente, fonte, externalId)`, quindi con più fonti lo stesso identificativo potrebbe restituire più righe (oggi la fonte è solo `file`, con lo SHA-256 esadecimale come identificativo). I limiti del filtro (da 1 a 128 caratteri, senza spazi) sono volutamente più stretti di quelli dell'importer (256 caratteri, spazi ammessi): lo SHA-256 esadecimale della fonte `file` ha 64 caratteri senza spazi; un identificativo più lungo o con spazi (una fonte futura) si può memorizzare ma non è raggiungibile con il filtro (400), e in quel caso i due limiti vanno riallineati. Il valore ricevuto non compare mai nelle risposte di errore né nei log.

Il documento OpenAPI (`/openapi/v1.json`, solo in Development) descrive `externalId` come stringa opzionale con `maxLength: 128`.

## Dettaglio e stream ridotto

`GET /activities/{id}?maxPoints=` restituisce i campi dell'elenco più `movingSeconds`, `summary` (frequenza cardiaca media e massima, potenza e cadenza medie, ciascuna nulla se assente), `laps` e `stream` (formato a colonne, come in `StreamColumns` dei contratti). Un id inesistente o di un altro utente risponde 404 (`urn:trimetric:problem:activity-not-found`).

- `maxPoints`: intero da 2 (il minimo, perché primo e ultimo punto stiano sempre entrambi) a `Activities:MaxStreamPoints` (predefinito 5000, configurabile da 1000 a 100.000, validato all'avvio); predefinito 1000 (REQ-018, AC-011).
- La riduzione avviene **nel database** (`ActivityStreamReader`): `row_number()` e indici a passo costante, con sempre il primo e l'ultimo punto; dal database escono al massimo `maxPoints` righe. Non è LTTB.
- **Ordine tra punti con lo stesso istante** (DEC-018): per `time` e, a parità, per i valori delle colonne (`ASC NULLS LAST`); non è l'ordine del file e non dipende dal layout fisico (non si usa `ctid`).
- Una colonna senza alcun valore nell'attività è `null`; con almeno un valore resta un array (anche se i punti scelti sono tutti nulli); con 0 punti `timeOffsetSeconds` è vuoto.
- Limite noto: `WHERE activity_id = @id` non ha un vincolo su `time` e visita tutti i chunk della hypertable (accettabile ora, da rivedere con molti chunk).

## Test

`dotnet test src/services/activities/tests/TriMetric.Activities.Tests`: richiede Docker o Podman avviato (TimescaleDB e NATS veri con Testcontainers, tramite `tests/TriMetric.TestInfrastructure`). Si possono filtrare per categoria (`--filter Category=Persistence`, `Consumer` o `Api`). I test non usano `TRUNCATE`: ogni test usa identificativi nuovi nel database condiviso del contenitore di prova (non in quello di sviluppo). Negli override di configurazione degli host di test usare `UseSetting` e non `ConfigureAppConfiguration` (arriva dopo la costruzione dell'host e viene ignorato, ad esempio per `Dev:UserId` e per `Consumers:*`). Gli scenari che coinvolgono anche `ingestion` e il gateway sono in `tests/TriMetric.IntegrationTests`.
