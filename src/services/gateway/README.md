# gateway

Punto d'ingresso unico verso i servizi (YARP). **Non contiene logica di business né database.**

- **Responsabilità:** routing `/api/<servizio>/...`, confine di sicurezza sull'identità del client, limite del corpo delle richieste. Previsti più avanti: validazione dei token, rate limiting, CORS, inoltro del webhook di Strava a `ingestion`.
- **Dati posseduti:** nessuno.
- **Eventi:** nessuno (nessuna dipendenza da NATS né da `Metrica.Messaging`).
- **Dipendenze:** solo `Yarp.ReverseProxy` e `Metrica.ServiceDefaults` (correlationId, `/health/live`, telemetria, log JSON).
- **Stato:** walking skeleton implementato (T14).

Vedi [`ARCHITECTURE/01-services.md`](../../../ARCHITECTURE/01-services.md).

## Rotte

Percorsi relativi; la query string arriva intatta (salvo `userId`, vedi sotto). Ogni percorso che non corrisponde a una rotta
risponde 404 dal gateway, senza contattare alcun backend (nessuna rotta generica `/api/{servizio}`). I metodi non sono ristretti:
un metodo inconsueto (TRACE, PROPFIND…) su un percorso valido arriva al backend, che lo rifiuta; `CONNECT` dà 502.

| Richiesta al gateway | Cluster | Il backend riceve |
|---|---|---|
| `/api/ingestion/{**remainder}` | `ingestion` (`http://127.0.0.1:5081`) | `/{**remainder}` (es. `POST /api/ingestion/uploads` → `POST /uploads`) |
| `/api/activities` e `/api/activities/{**remainder}` | `activities` (`http://127.0.0.1:5082`) | `/activities/{**remainder}` (es. `/api/activities` → `/activities`, `/api/activities/{id}` → `/activities/{id}`) |

- Un'unica rotta per servizio: `/api/activities/{**remainder}` accetta anche `/api/activities` senza resto. Il prefisso si toglie con
  `PathRemovePrefix` (per activities solo `/api`, così `/activities` resta nel percorso).
- `/api/activitiesXYZ` e `/api/ingestionXYZ` **non** corrispondono (il confine è il segmento): 404 dal gateway.
- **Barra finale** (conservata, mai raddoppiata): `/api/activities/` → il backend riceve `/activities/`; `/api/activities/{id}/` →
  `/activities/{id}/`; `/api/ingestion/uploads/` → `/uploads/`. ASP.NET Core ignora la barra finale nelle rotte (verificato con un
  test su una minimal API vera), quindi `GET /activities/` risponde come `GET /activities`.
- `/api/ingestion` e `/api/ingestion/` (senza resto) inoltrano `/` a ingestion (404 del servizio). Con `/{**remainder}` il gateway
  inoltra qualunque percorso di ingestion, quindi anche le sue `/health/*` e, in Development, `/openapi/v1.json` sono raggiungibili da
  `/api/ingestion/...`: nessun dato sensibile, ma va ricordato se si restringe la superficie.
- Metodi inoltrati con lo stesso metodo (GET, POST, PUT, PATCH, DELETE, HEAD verificati).
- Risposte dei backend inoltrate com'è (stato, corpo `ProblemDetails`, intestazioni di contenuto). Backend irraggiungibile → 502 con
  corpo vuoto (nessun nome di host né stack); backend fermo oltre l'`ActivityTimeout` → 504.

## Confine di sicurezza sull'identità (REQ-021)

I backend ricavano l'utente solo dal proprio principal; il gateway non aggiunge mai identità e toglie quella dichiarata dal client:

- **Intestazioni rimosse** (confronto senza distinzione di maiuscole, per tutte le rotte, anche quelle aggiunte dal file di
  configurazione, anche se una trasformazione di configurazione prova a impostarle): quelle che iniziano per `X-User`, `X-Auth`,
  `X-Identity`, `X-Claim`, `X-Principal`, `X-Authenticated`, più `X-Forwarded-User`, `X-Remote-User`, `X-Claims` e, in più rispetto al
  minimo del piano, `X-Forwarded-Email`, `X-Forwarded-Preferred-Username`, `X-Forwarded-Groups`, `Remote-User`. Una `X-Authority` o una
  `X-Username` qualunque sono quindi rimosse (la regola è sul prefisso).
- **Parametro di query `userId`** rimosso (senza distinzione di maiuscole, anche ripetuto o con il nome codificato, es. `user%49d`);
  gli altri parametri restano come il client li ha inviati (nessuna ricodifica di `%20`, `+`, ripetizioni), tranne le sequenze `%`
  non valide (es. `%ZZ`), che YARP/ASP.NET ricodificano come `%25ZZ`. `?userId=x` da solo → nessuna query.
- **Non è un elenco esaustivo:** i nomi con underscore (`X_User_Id`) e altre intestazioni di identità di proxy specifici (`X-Original-User`,
  `X-MS-CLIENT-PRINCIPAL-ID`, …) passano. È una rete di sicurezza: i backend ricavano l'utente solo dal proprio principal e non leggono
  nessuna di queste intestazioni (REQ-021).
- **Passano:** `Authorization` (invariata), `Content-*`, `X-Forwarded-For/Proto/Host` (impostate da YARP, sovrascrivono quelle del client).
- **`X-Correlation-Id`:** il correlationId valido del client, o quello generato da ServiceDefaults, viene impostato anche sulla richiesta
  verso il backend (stesso valore della risposta al client); un valore non valido del client non arriva ai backend.
- **Log:** nessun valore di intestazione, query, corpo o nome di file; `Yarp.ReverseProxy` è filtrato a Warning in codice (a Information
  registrerebbe l'URL di destinazione con la query).

## Limite del corpo (REQ-020, T10)

`Gateway:MaxRequestBodyBytes` = **34.603.008 byte** (`Upload:MaxBytes` di ingestion, 32 MiB, più 1 MiB di overhead multipart); valido tra 1 e
68.157.440 (64 MiB + 1 MiB); validato all'avvio, chiavi sconosciute nella sezione `Gateway` rifiutate. Il limite vale per tutte le rotte:

- `Content-Length` oltre il limite → 413 `ProblemDetails` **prima** di contattare il backend (connessione chiusa, corpo non letto).
- Senza `Content-Length` (chunked) → il limite globale di Kestrel interrompe la lettura e risponde 413; il backend ha ricevuto al più il
  corpo fino al limite e la richiesta non si completa. **Kestrel conta anche il framing del chunked** (circa 9 byte ogni 64 KiB): un corpo
  chunked di esattamente 34.603.008 byte di dati viene rifiutato. Con `Content-Length` il limite è esatto.
- **Il 413 dei corpi grandi non è leggibile dal client.** Il gateway (come ingestion) risponde senza leggere il corpo e Kestrel
  chiude la connessione: un `HttpClient` o un `fetch` che sta ancora inviando decine di MiB vede un errore di rete
  («connection aborted» / `TypeError: Failed to fetch`), non lo stato 413 (provato: upload di 40 MiB, 12 tentativi su 12). Il 413 si legge
  con `Expect: 100-continue` o quando il corpo supera il limite di pochi byte. Il client web deve quindi controllare la dimensione
  PRIMA di inviare (limite 32 MiB, T17) e trattare un errore di rete su un upload grande come «file troppo grande o connessione interrotta».
- Il corpo sotto il limite attraversa il gateway **in streaming**, senza bufferizzazione, e arriva ai backend identico byte per byte.

## Timeout

- `ActivityTimeout` di YARP esplicito nei cluster: 100 s (`00:01:40`, il valore predefinito di YARP). È un timeout di **inattività**: si
  riazzera a ogni operazione sul corpo o sulla risposta. Un upload lento ma che avanza non viene tagliato, per quanto duri in totale; uno
  fermo per oltre 100 s sì. Nessun `Timeout` totale sulla rotta.
- Resta il minimo di velocità di Kestrel sul corpo (240 byte/s dopo 5 s di tolleranza) contro i client che inviano a goccia.

## Configurazione

`appsettings.json`: Kestrel **solo** su `http://localhost:5080` (DEC-009: il gateway ascolta su `localhost`, i servizi su `127.0.0.1`;
mai `0.0.0.0`), `Gateway:MaxRequestBodyBytes` e la sezione `ReverseProxy` (rotte e cluster, indirizzi `127.0.0.1:5081` e `127.0.0.1:5082`).
`AllowedHosts` è `*` (limite noto: nessuna restrizione dell'`Host`; ristretto a `localhost;127.0.0.1` fermerebbe il DNS rebinding, non il CSRF). In `Development`, con l'identità fissa dei servizi, un form cross-site (multipart, senza preflight) verso `http://localhost:5080` è accettato e `ingestion` dichiara `DisableAntiforgery()`: nessuna mitigazione presente oggi (servirebbero un controllo di `Origin` e/o un token anti-falsificazione, oppure un'identità reale con token Bearer **[Ipotesi]**). Nessun `appsettings.Development.json`: non c'è nulla di specifico dell'ambiente, né segreti né identità di sviluppo (il gateway non autentica: l'identità la fornisce ogni servizio). Il dev server di Vite
inoltra `/api` al gateway. Per ridefinire un indirizzo: `ReverseProxy:Clusters:<ingestion|activities>:Destinations:primary:Address`.

## Salute

`/health/live` risponde 200 senza chiamare i backend. `/health/ready` (di ServiceDefaults) non ha controlli registrati e risponde 200 finché
il processo è vivo: il gateway non ha database e non sonda i backend, la cui salute è nei rispettivi `/health/ready`.

## Test

`dotnet test src/services/gateway/tests/Metrica.Gateway.Tests`: server di prova veri su loopback con porta effimera al posto dei servizi
(nessun contenitore, nessun servizio reale; le porte 5080-5082 non si usano).
