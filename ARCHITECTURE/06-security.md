# 06 · Sicurezza e privacy

## Autenticazione

Nel PoC l'accesso avviene tramite Strava (OAuth) con un `JWT_SECRET` nel backend. Nel nuovo progetto:

- **Accesso con Strava** come metodo principale; email e password come alternativa **[Ipotesi]**.
- `identity` emette **token di accesso brevi** (minuti) e **token di refresh** ruotati a ogni uso.
- Il gateway valida il token di accesso; i servizi a valle ne riverificano la firma e leggono l'identità da lì, senza fidarsi di intestazioni non firmate.
- Il frontend non conserva token in `localStorage`: **[Ipotesi]** cookie `HttpOnly`, `Secure`, `SameSite` per il refresh, token di accesso in memoria.

**Decisione aperta:** implementare `identity` con **ASP.NET Core Identity + OpenIddict** (controllo totale, valore didattico) oppure usare un provider esterno (es. Keycloak, Auth0). Il primo è più impegnativo ma coerente con l'obiettivo di apprendimento; va affrontato con attenzione alla sicurezza.

## Autorizzazione

- Ogni dato appartiene a un `userId`; ogni query filtra per l'utente autenticato. È una regola da testare **per ogni endpoint** (un utente non deve mai leggere dati altrui).
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
- **Registri:** nei log niente token, nessuna coordinata, nessun dato sanitario.
- **Backup:** soggetti alla stessa politica di conservazione.

## Sicurezza della catena di build

- Dipendenze controllate (audit di NuGet e pnpm), aggiornamenti regolari.
- Immagini minime, utente non privilegiato, nessun segreto nelle immagini.
- Scansione delle immagini nella pipeline **[Ipotesi]**.

## Minacce principali da tenere presenti

| Minaccia | Mitigazione |
|---|---|
| Un utente legge dati di un altro | Filtro per `userId` ovunque, test dedicati |
| Furto dei token Strava | Cifratura a riposo, ambiti minimi, revoca |
| Webhook falsi | Token di verifica, nessuna fiducia nel corpo |
| Furto di sessione | Cookie `HttpOnly`, token brevi, rotazione del refresh |
| File FIT/GPX malevoli | Limiti di dimensione, parsing isolato e con tempo massimo, nessuna esecuzione |
| Fuga di dati nei log | Log strutturati con campi vietati |
