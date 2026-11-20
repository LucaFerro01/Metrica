# 06 · Sicurezza e privacy

## Stato nello scheletro

Il servizio `identity` non esiste: non c'è login, né token, né cookie. Ciò che c'è, verificato dal codice e dai test, e ciò che invece è **previsto** o **[Ipotesi]** nelle sezioni sotto (vocabolario in [README](README.md#come-si-usa-questa-cartella)):

| Aspetto | Stato reale |
|---|---|
| Identità | In `Development` ogni richiesta ai servizi è autenticata come l'utente fisso `Dev:UserId` (`AddDevelopmentIdentity`, DEC-006, REQ-021). Fuori da `Development` lo schema predefinito è `Deny` e ogni richiesta protetta riceve **401** (provato per gli endpoint di `ingestion` e `activities`, AC-009). `IsDevelopment()` non distingue le maiuscole: `ASPNETCORE_ENVIRONMENT` non va mai impostata a `Development` in produzione |
| `userId` | I servizi lo leggono **solo dal principal** (`GetUserId`, rigido: UUID formato `D` non vuoto, un solo claim `sub`), mai da corpo, query o intestazioni; un campo `userId` nel form, nella query o in un'intestazione è ignorato (DEC-006) |
| Confine d'identità del gateway | Il gateway **non aggiunge mai** identità e toglie quella dichiarata dal client (REQ-021): intestazioni `X-User*`, `X-Auth*`, `X-Identity*`, `X-Claim*`, `X-Principal*`, `X-Authenticated*` e alcune `X-Forwarded-*`/`Remote-User`, più il parametro di query `userId` (anche ripetuto o con il nome codificato) su tutte le rotte; l'elenco non è esaustivo, ma i backend non leggono quelle intestazioni. `Authorization` passa invariata (oggi nessun servizio la usa) |
| Isolamento tra utenti | Ogni query di `ingestion` e `activities` è filtrata per `userId`; un'attività altrui risponde 404 come un id inesistente (AC-007, test in ogni servizio e negli scenari di integrazione) |
| Isolamento tra servizi | Un ruolo di database per servizio, con permessi solo sul proprio schema (AC-008, errore `42501` in entrambe le direzioni, anche sui chunk della hypertable); nessun ruolo ha `CREATE` su `public` |
| Limiti di dimensione e parsing | Limite del corpo del gateway (32 MiB più 1 MiB) e di `ingestion` (`Upload:MaxBytes`, 32 MiB); `XmlReader` indurito per il GPX (nessun DTD né resolver, profondità massima 32); tetti su punti (100.000), giri e sessioni; valori fuori scala trattati come assenti; tempo massimo di parsing (422 allo scadere); corpo multipart letto in streaming con tre tetti; nessuna esecuzione dei file. Il parsing avviene nello stesso processo, non è isolato |
| Privacy dei log | Nei log niente coordinate, frequenza cardiaca, contenuto o nome dei file, valori di query e intestazioni (REQ-034): provato con provider di log a livello `Trace` sui tre servizi (`LogPrivacyTests`, test di integrazione e rilevatore). `Microsoft.AspNetCore.Hosting.Diagnostics` è a `Warning`, `Yarp.ReverseProxy` a `Warning`, le query EF a `Warning`, nessun `EnableSensitiveDataLogging` (una guardia di repository lo vieta) |
| Esposizione di rete | Solo `127.0.0.1`/`localhost`: servizi, gateway e web per configurazione, PostgreSQL e NATS per `deploy/compose.yaml` (DEC-009). Il dev server non va esposto con `--host`: chi raggiunge il computer agirebbe come l'utente di sviluppo |
| Segreti | Nessun segreto nel repository; password `dev_only_*` di sola sviluppo, NATS senza autenticazione |

**Limiti noti dello scheletro** (non risolti): `AllowedHosts` è `*` in tutti e tre i servizi e il gateway non restringe l'`Host`: **DNS rebinding** locale possibile (mitigazione: `AllowedHosts` ristretto a `localhost;127.0.0.1`, che però **non** ferma il CSRF). **CSRF:** in `Development` l'identità fissa vale per chiunque raggiunga `localhost:5080`, e un form cross-site verso `http://localhost:5080` ha `Host: localhost:5080` e invia un multipart, che è una richiesta «semplice» senza preflight; l'endpoint di upload dichiara esplicitamente `DisableAntiforgery()`. **Nessuna mitigazione è presente oggi**; il rischio esiste solo in `Development`, con identità fissa. Per chiuderlo servono un controllo di `Origin` e/o un token anti-falsificazione, oppure non usare un'identità fissa: con `identity` reale e un token Bearer in `Authorization` il CSRF da cookie non si pone **[Ipotesi]**; la rotta del gateway verso `ingestion` è `/api/ingestion/{**remainder}` e quindi espone anche le `/health/*` di `ingestion` (e, in `Development`, il suo `/openapi/v1.json`), senza autenticazione e senza dettagli; i metodi HTTP non sono ristretti; nessuna prova con TLS o HTTP/2; il percorso con l'id dell'attività compare nello scope dei log `Information`; nessun limite di concorrenza sugli upload (circa 3 volte la dimensione del file in memoria per richiesta). Le tracce OpenTelemetry registrano `url.query` secondo le note di T03 (non riverificato qui): nessun endpoint attuale mette dati personali nella query. Dettagli nei README di [`gateway`](../src/services/gateway/README.md) e [`ingestion`](../src/services/ingestion/README.md).

## Autenticazione

Nel PoC l'accesso avviene tramite Strava (OAuth) con un `JWT_SECRET` nel backend. Nel nuovo progetto (**tutto quanto segue è previsto, non implementato**):

- **Accesso con Strava** come metodo principale; email e password come alternativa **[Ipotesi]**.
- `identity` emette **token di accesso brevi** (minuti) e **token di refresh** ruotati a ogni uso.
- Il gateway valida il token di accesso; i servizi a valle ne riverificano la firma e leggono l'identità da lì, senza fidarsi di intestazioni non firmate.
- Il frontend non conserva token in `localStorage`: **[Ipotesi]** cookie `HttpOnly`, `Secure`, `SameSite` per il refresh, token di accesso in memoria.

**Decisione aperta:** implementare `identity` con **ASP.NET Core Identity + OpenIddict** (controllo totale, valore didattico) oppure usare un provider esterno (es. Keycloak, Auth0). Il primo è più impegnativo ma coerente con l'obiettivo di apprendimento; va affrontato con attenzione alla sicurezza.

## Autorizzazione

- Ogni dato appartiene a un `userId`; ogni query filtra per l'utente autenticato. È una regola da testare **per ogni endpoint** (un utente non deve mai leggere dati altrui): realizzata e provata per gli endpoint di `ingestion` e `activities` esistenti (vedi *Stato nello scheletro*).
- Ruoli iniziali: solo `athlete`. Un ruolo `coach` (che vede i dati di più atleti con consenso) è una possibile evoluzione che richiede un modello di condivisione esplicito.

## Token dei servizi esterni

- I token OAuth di Strava e Zepp sono **cifrati a riposo** (chiave fuori dal database) e visibili solo a `identity` e al connettore che li usa **[Ipotesi]**.
- Scadenza e rinnovo gestiti dal servizio proprietario; la revoca da parte dell'utente (o da Strava) pubblica `ConnectionRevoked` e ferma la sincronizzazione.
- Si richiedono gli **ambiti minimi** necessari (es. lettura delle attività).

## Webhook

- Verifica del token di sottoscrizione richiesto da Strava.
- Il corpo del webhook **non è considerato affidabile**: contiene solo identificativi; i dati si scaricano dall'API con il token dell'utente.
- Deduplicazione e limite di richieste per evitare abusi.

## Dati personali (GDPR)

I dati di allenamento (frequenza cardiaca, peso, posizione) possono essere dati relativi alla salute e vanno trattati con cautela.

- **Minimizzazione:** si conserva ciò che serve alle funzioni.
- **Cancellazione:** `UserDeleted` fa cancellare i dati da tutti i servizi, compresi file grezzi e copie locali.
- **Portabilità:** esportazione dei dati dell'utente in formato aperto (JSON e file FIT/GPX originali).
- **Posizione:** possibilità di nascondere l'inizio e la fine dei percorsi (zone private), come fa Strava.
- **Registri:** nei log niente token, nessuna coordinata, nessun dato sanitario. *Realizzato e provato per i tre servizi attuali* (REQ-034, vedi *Stato nello scheletro*); i file grezzi non sono conservati (si salvano solo nome e dimensione del file nel registro dei caricamenti).
- **Backup:** soggetti alla stessa politica di conservazione.

## Sicurezza della catena di build

- Dipendenze controllate (audit di NuGet e pnpm), aggiornamenti regolari.
- Immagini minime, utente non privilegiato, nessun segreto nelle immagini.
- Scansione delle immagini nella pipeline **[Ipotesi]**.

## Minacce principali da tenere presenti

| Minaccia | Mitigazione |
|---|---|
| Un utente legge dati di un altro | Filtro per `userId` ovunque, test dedicati (realizzato per `ingestion` e `activities`) |
| Furto dei token Strava | Cifratura a riposo, ambiti minimi, revoca |
| Webhook falsi | Token di verifica, nessuna fiducia nel corpo |
| Furto di sessione | Cookie `HttpOnly`, token brevi, rotazione del refresh |
| File FIT/GPX malevoli | Limiti di dimensione, parsing con tempo massimo, nessuna esecuzione (realizzati); parsing isolato in un altro processo **[Ipotesi]** (oggi nello stesso processo) |
| Fuga di dati nei log | Log strutturati con campi vietati (realizzato: nessun dato personale nei log, provato con log a livello `Trace`) |

Le mitigazioni su token Strava, webhook e sessione (cifratura, token di verifica, cookie `HttpOnly`, token brevi) sono **previste**: Strava, `identity` e il webhook non esistono ancora. Restano **[Ipotesi]** anche CORS e rate limiting del gateway, la validazione dei token nel gateway, la cancellazione e l'esportazione dei dati (`UserDeleted`), le zone private e la scansione delle immagini.
