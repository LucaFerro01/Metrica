# activities

Archivio canonico delle attività e delle loro serie temporali.

- **Responsabilità:** salvataggio di attività, stream e giri; API di lettura (elenco con filtri, dettaglio, stream ridotti per i grafici); correzioni manuali.
- **Dati posseduti:** schema `activities` (attività, stream come hypertable Timescale, giri).
- **Pubblica:** `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`.
- **Consuma:** `ActivityImported`, `UserDeleted`.
- **Origine nel PoC:** `activities.routes.ts`, tabelle attività in `schema.ts`.
- **Stato (scheletro walking-skeleton-import):** implementati l'importazione di `ActivityImported` (consumatore con inbox e scrittura di attività, giri e stream) e le API di lettura (elenco con filtri, dettaglio con stream ridotto e filtro `externalId`, DEC-017). Non ancora implementati: la pubblicazione di `ActivityStored`, `ActivityUpdated` e `ActivityDeleted`, il consumo di `UserDeleted` e le correzioni manuali.

Vedi [`03-data.md`](../../../ARCHITECTURE/03-data.md).

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
