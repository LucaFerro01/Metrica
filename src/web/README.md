# web

Frontend dell'applicazione: **React 19 + TypeScript (strict) + Vite**, con Tailwind CSS 4, TanStack Router e Query, `openapi-fetch` con tipi generati, `i18next`, `lucide-react`. Un'unica app responsiva con due layout (telefono sotto 768 px, desktop da 768 px), predisposta per un guscio nativo Capacitor che si aggiunge dopo lo scheletro (T21); nessuna PWA né service worker (DEC-015, DEC-016).

- **Stato:** scaffold (T16), pagina Carica (T17) ed elenco e dettaglio delle attività con righe «in elaborazione» (T18): lo scheletro del web è completo, vedi le sezioni sotto. Gli altri ambiti (analisi, pianificazione, profilo, accesso) non esistono ancora.
- **Prerequisiti:** Node 22 o più recente, pnpm 12 (`packageManager` è fissato in `package.json`).
- **Dialoga solo con il gateway** (`/api`, inoltrato dal dev server a `http://127.0.0.1:5080`).

## Comandi

Dalla radice del repository (`pnpm -C src/web <script>`) o da `src/web`.

| Comando | Cosa fa |
|---|---|
| `pnpm install --frozen-lockfile` | Installa le dipendenze esattamente come da `pnpm-lock.yaml`. |
| `pnpm dev` | Dev server su `http://localhost:5173` (porta fissa; `/api` → gateway `127.0.0.1:5080`). **Non usare `--host`**: i servizi usano l'identità di sviluppo fissa (DEC-015). |
| `pnpm build` | `tsc -b` e build di produzione in `dist/` (ignorata da git). |
| `pnpm lint` | ESLint (flat config) con le regole di architettura e di accesso al browser. |
| `pnpm exec tsc -b` | Solo controllo dei tipi. |
| `pnpm test` | Vitest + Testing Library (jsdom). Esclude `tests/e2e`. |
| `pnpm test:e2e` | Playwright (`tests/e2e/`: `smoke.spec.ts`, `upload.spec.ts`, `activities.spec.ts`, `real-stack.spec.ts`), ognuno a 375 px e a 1280 px. **Richiede i browser**: `pnpm exec playwright install chromium` (centinaia di MB, una tantum). Avvia (o riusa) il dev server su 5173. Smoke, upload e activities intercettano `/api/*` con `page.route` (solo i percorsi che iniziano con `/api/`, non i moduli di Vite): non servono servizi. `real-stack.spec.ts` si salta salvo `E2E_REAL=1` (vedi *Prova sullo stack reale*). |
| `pnpm gen:api` | Rigenera `src/shared/api/ingestion.d.ts` e `activities.d.ts` dai documenti OpenAPI di `ingestion` (`:5081`) e `activities` (`:5082`). Servono i due servizi avviati in `Development`. Da eseguire quando cambiano le API, poi rivedere il diff. |

## Pagina Carica (T17)

`src/features/activities/upload/` (REQ-026, REQ-030, REQ-037, AC-001, AC-004):

- **Scelta:** un solo file `.gpx`/`.fit` con il normale `<input type="file" accept=".gpx,.fit">` (nascosto visivamente, etichettato dal pulsante «Scegli un file», bersaglio da 44 px; sul telefono apre il selettore di sistema) e, da 768 px, anche trascinamento (drop) sull'area tratteggiata, con la stessa validazione. Con più file trascinati si prende il primo; durante l'invio il drop è ignorato. L'area si evidenzia solo se il trascinamento contiene file; un file lasciato FUORI dall'area non fa navigare il browser (gestore su `window` per `dragover`/`drop` con file, rimosso allo smontaggio).
- **Validazione PRIMA di inviare** (`limits.ts`): estensione `.gpx`/`.fit` senza distinzione di maiuscole, file non vuoto, **al massimo 32 MiB = 33.554.432 byte** (esattamente 32 MiB è ammesso). `MAX_UPLOAD_BYTES` è l'unico punto e rimanda a `Upload:MaxBytes` di ingestion. Il limite sul client è indispensabile: attraverso il gateway il 413 di un file grande **non è leggibile** dal browser (rilievo I1 di T14: il gateway chiude la connessione senza leggere il corpo).
- **Invio** (`uploadFile.ts`): `XMLHttpRequest` (serve l'avanzamento dell'invio, `fetch` non lo dà) verso `<base ingestion>/uploads` (`ingestionBaseUrl` di `shared/api/config`), `FormData` con campo `file`, `upload.onprogress`, annullabile con `AbortSignal` (nessun timeout lato client: l'unico modo di interrompere un invio è «Annulla»). Non lancia mai: restituisce un `UploadResult` (successo, annullato, rete, errore con codice, errore HTTP senza codice, risposta inattesa); `UploadResponse.status` (`string` nel tipo generato) si restringe con un type guard. **Uscendo dalla pagina durante l'invio la richiesta NON si interrompe**: se il server accetta (202) l'upload si registra comunque in `pendingUploads` e dopo lo smontaggio non si aggiorna nessuno stato React. Un secondo invio mentre uno è in corso è ignorato (guard su un riferimento sincrono, anche con due clic nello stesso istante).
- **Esiti e messaggi** (`uploadMessages.ts`, testi in `it.json`/`en.json` sotto `activities.upload`): `accepted` (202) «caricato, in elaborazione»; `duplicate` (200) avviso; `ProblemDetails` mappati per `type` (`urn:trimetric:problem:<codice>`, 10 codici) sui messaggi tradotti, **mai** il `title`/`detail` del server; 413 anche senza `type` di TriMetric (quello del gateway); 502/504/400 a corpo vuoto → messaggi per stato; altri 4xx/5xx → messaggio per classe; 200 con corpo inatteso → «risposta inattesa»; errore di rete → «connessione interrotta»; **con un file oltre 8 MiB** (soglia arbitraria) lo stesso testo neutro con in più il suggerimento di riprovare con una rete più stabile e il limite (non si dà la colpa alla dimensione: con il limite del client uguale a quello del server il 413 illeggibile non può nascere da un file che supera il controllo; un test verifica che `MAX_UPLOAD_BYTES` coincida con `Upload:MaxBytes` di `appsettings.json` di ingestion). Nessun nome di file né testo di eccezione nei messaggi, nessuna scrittura sulla console.
- **Avanzamento:** barra determinata con `aria-valuenow`; se il browser non conosce la lunghezza la barra è indeterminata (senza `aria-valuenow`) con «Invio in corso…»; a 100% mostra «Elaborazione sul server…» finché non arriva l'esito.
- **Focus da tastiera:** all'avvio dell'invio il focus va a «Annulla»; all'esito al messaggio (contenitore con `tabIndex={-1}`); dopo un errore al messaggio d'errore; dopo «Annulla» a «Invia file». Il pulsante di invio si chiama «Invia file» / «Send file» per non confondersi con la voce di navigazione «Carica» / «Upload».
- **Stato:** `role="progressbar"` con `aria-valuenow`/`min`/`max`, esito in una regione `role="status"` (`aria-live="polite"`, sempre presente), errori in `role="alert"`. Dopo un esito positivo il file si deseleziona (niente doppio invio) e compare il link «Vai alle attività»; dopo un errore il file resta scelto e si può riprovare.

## Elenco e dettaglio delle attività (T18)

`src/features/activities/{data,display,list,detail}/` (REQ-027, REQ-028, REQ-030, REQ-037, REQ-040, AC-001, AC-010; DEC-008, DEC-015, DEC-017):

- **Route:** `/activities?sport=&page=` e `/activities/$activityId`. I parametri si validano in `data/activitiesSearch.ts` (`validateSearch` del router): `sport` solo `run|ride|swim|other` (esatto, minuscolo), `page` intero tra 2 e 1.000.000 (`page=1` è il predefinito e non si riporta); **un valore non valido non è mai un errore**: si scarta e valgono i predefiniti. La funzione restituisce sempre entrambe le chiavi (anche `undefined`) perché il router fonde il risultato sopra i parametri grezzi ereditati: una chiave mancante lascerebbe passare `?page=0`. Il dettaglio accetta gli stessi due parametri solo per il link «Torna all'elenco», che li conserva.
- **Dati:** TanStack Query con il client generato (`activitiesClient`). `data/activitiesApi.ts` valida la forma della risposta e traduce ogni errore in un `kind` (`network`, `bad-request`, `not-found`, `server`, `unexpected`): si decide solo dallo stato HTTP (gli errori del gateway hanno il corpo vuoto) e non si mostra mai testo del server. Nessun tentativo automatico (`retry: false`): l'errore si vede subito, con «Riprova». Elenco con `pageSize=20` esplicito; `keepPreviousData` (l'elenco resta, attenuato, mentre arriva il nuovo filtro o la nuova pagina). `shared/api/client.ts`: il client di `activities` usa il `fetch` globale corrente (i test lo sostituiscono con `vi.stubGlobal`) e rende assoluta la base relativa `/api` rispetto all'origine della pagina (nel browser è equivalente; in jsdom `new Request('/api/...')` lancia).
- **Elenco:** data e ora, sport (testo tradotto), distanza, durata, dislivello (assente → «non disponibile»), nell'ordine del server. **Schede sotto 768 px, tabella da 768 px**: due rendering con lo stesso contenuto (`md:hidden` e `hidden md:block`; quello nascosto è `display: none`, quindi fuori dall'albero di accessibilità). Ogni riga è un link a `/activities/$id`; in tabella è cliccabile tutta la riga col mouse (non se c'è del testo selezionato) e il link sulla prima cella serve a tastiera e screen reader; il contenitore della tabella ha `overflow-x-auto` (mai `overflow-hidden`, che taglierebbe i dati senza segnalarlo) e un test e2e verifica che a 768 e 1280 px con dati realistici non scorra. Filtro per sport (Tutti, Corsa, Bici, Nuoto, Altro, bersagli da 44 px) che riparte da pagina 1; paginazione «Precedente», «Pagina X di Y», «Successiva», disabilitate ai bordi; una pagina oltre l'ultima spiega che non esiste e offre «Vai all'ultima pagina». Stati: caricamento (`role="status"`), vuoto («Nessuna attività: carica un file», con link a Carica; con filtro, «Nessuna attività per questo sport»), errore (`role="alert"` con «Riprova»).
- **Righe «in elaborazione» (DEC-008, DEC-017):** `list/usePendingTracking.ts` segue gli upload di `pendingUploads` con **un solo meccanismo**: per ogni upload una query `GET /activities?externalId=<id>&pageSize=1` (rilevamento esatto: NON cerca nella pagina visibile, quindi trova anche un file con data vecchia che l'ordine per data di inizio colloca in una pagina successiva). Finché non è scaduto si ripete **ogni 2 s** (`refetchInterval`; TanStack Query lo sospende a scheda nascosta), al massimo una richiesta per upload e per ciclo, senza accumulo se una richiesta è lenta. Quando l'attività esiste si invalida l'elenco (che si riscarica senza polling proprio) e **solo dopo** si toglie l'upload dallo store, così la riga «in elaborazione» non sparisce prima che compaia quella vera. **Timeout 60 s da `acceptedAt`** (`Date.now() - acceptedAt`, quindi dall'accettazione e non dall'avvio dell'invio; l'importazione dei file normali richiede pochi secondi): il polling si ferma e si esegue **un ultimo controllo automatico** per riga (il tempo scorre anche a scheda nascosta mentre il polling è sospeso: senza questo controllo si direbbe «ancora in elaborazione» senza aver interrogato il server; l'orologio si riallinea anche al `visibilitychange`, perché sul telefono i timer di una scheda in secondo piano possono essere sospesi). Solo se l'attività ancora non c'è (o il controllo fallisce) la riga diventa «Ancora in elaborazione.» con «Riprova», che esegue un controllo singolo (se l'attività esiste la risolve); durante l'ultimo controllo la riga resta «in elaborazione». Un upload già scaduto all'apertura dell'elenco usa il controllo di montaggio come ultimo controllo (una richiesta, nessun polling). Con lo store vuoto non esiste nessuna query: nessuna richiesta. Le righe si **mostrano solo a pagina 1 e senza filtro per sport** (altrimenti sembrerebbero far parte di un'altra selezione); il rilevamento invece prosegue comunque. Un errore del rilevamento non è un errore dell'elenco e non ferma il polling. Un orologio (`useNow`) si risveglia solo alla prossima scadenza (e al ritorno in primo piano), rileggendo `Date.now()` con `Math.max` perché un timer può scattare 1 ms in anticipo: nessun intervallo permanente. Una regione `aria-live="polite"` `sr-only` sempre presente nella pagina annuncia «Attività importata» e «ancora in elaborazione» (un `role="status"` inserito dopo il caricamento non è annunciato in modo affidabile).
  - **Limite (M7):** il rilevamento «in elaborazione» e il relativo aggiornamento valgono **solo per gli upload tracciati nella sessione** (lo store è in memoria): dopo un ricaricamento della pagina, o con un upload fatto da un altro dispositivo o scheda, l'elenco non si aggiorna da solo e serve ricaricarlo. Nel dev server il `StrictMode` di React raddoppia la prima richiesta di ogni query (smonta e rimonta, e la richiesta con `signal` si annulla e riparte): in produzione non succede.
- **Dettaglio:** numeri grandi (`text-2xl`, `md:text-3xl`) con data e ora, sport, distanza, durata, durata in movimento, dislivello, FC, potenza e cadenza medie; «non disponibile» per i campi nulli (anche un riepilogo del tutto assente non dà errori); sezione «Giri» (numero, inizio relativo all'attività, durata, distanza) solo se ce ne sono. **Niente grafici, mappe né stream** (fuori ambito): la richiesta usa `?maxPoints=2`, il minimo ammesso dal server, invece dei 1000 punti predefiniti. 404 (identico per un id inesistente e per quello di un altro utente) e id che non è un UUID (nessuna chiamata di rete) → «Attività non trovata» con link all'elenco; altri errori → messaggio tradotto con «Riprova» (non è «non trovata»).
- **DEC-018:** nel dettaglio il server ordina i punti con lo stesso istante per valori e non nell'ordine del file; qui non si mostrano punti, quindi non ha effetto sul web. Se in futuro compariranno punti (grafici, mappe), l'ordine tra punti allo stesso istante non è quello del file.

### Upload in sospeso (per l'elenco, T18)

`src/features/activities/pending/pendingUploads.ts`: store **in memoria** (`useSyncExternalStore`) degli upload accettati (`accepted`, non `duplicate`): `{ externalId, uploadId, acceptedAt }` (`acceptedAt` = `Date.now()` alla ricezione del 202, NON all'avvio dell'invio), **mai il nome del file né il contenuto**. API: `pendingUploads.add / remove(externalId) / clear / getSnapshot / subscribe` e hook `usePendingUploads()`; esportati da `@/features/activities`. Lo stesso `externalId` non si duplica (resta il primo). **Solo in memoria, di proposito:** niente `localStorage`/`sessionStorage` (vietati alla feature dalla regola di lint, e il guscio nativo non li garantisce), quindi ricaricare la pagina azzera lo stato; la fonte di verità resta l'elenco dal server. Chi lo usa è l'elenco (T18, sezione sopra): una riga «in elaborazione» per ogni upload in sospeso, `remove(externalId)` quando `GET /activities?externalId=` trova l'attività; allo scadere del timeout (60 s da `acceptedAt`) la riga resta come «ancora in elaborazione» finché non si risolve. Un upload può essere registrato anche se l'utente ha lasciato la pagina Carica prima della risposta.

### Prova sullo stack reale

`tests/e2e/real-stack.spec.ts` (si salta salvo `E2E_REAL=1`, gira solo nel progetto desktop): Vite → gateway → ingestion → NATS → activities, nessuna intercettazione. Carica un GPX di esempio reso unico a ogni esecuzione (UUID nel `creator`): `accepted`, l'attività arriva in `activities` (il totale di `GET /api/activities` cresce), di nuovo → `duplicate`; un GPX non ben formato → 422 con messaggio tradotto; **AC-001**: dopo l'upload l'attività compare nell'elenco entro pochi secondi con data, sport, distanza e durata e il dettaglio si apre; un GPX con tracce del 2020 (generato dal test) compare comunque, con la riga «in elaborazione» risolta tramite `externalId`. Ogni esecuzione aggiunge tre attività al database di sviluppo (volume `trimetric_postgres-data`). Con lo stack avviato (`docker compose -f deploy/compose.yaml up -d`, poi i tre servizi con `dotnet run --no-build --project <csproj>` uno alla volta, poi `pnpm dev`):

```powershell
$env:E2E_REAL = '1'; pnpm -C src/web exec playwright test real-stack
```

## Struttura

```text
src/web/
├── index.html            viewport-fit=cover; script inline che applica il tema prima del primo disegno
├── .env.example          VITE_API_BASE_URL=/api
├── src/
│   ├── main.tsx
│   ├── index.css         Tailwind e variabili di tema (UNICO punto dei colori, chiaro e scuro)
│   ├── app/              router, provider, layout (AppShell, BottomNav, SideNav, ThemeToggle, LanguageSwitch)
│   ├── features/
│   │   └── activities/   upload/, pending/ (upload in sospeso), data/ (API, ricerca), display/, list/, detail/ + index.ts (interfaccia pubblica)
│   ├── shared/
│   │   ├── api/          config.ts (base URL), client.ts, tipi generati *.d.ts (committati)
│   │   ├── i18n/         init, locales/it.json e en.json
│   │   ├── lib/          format.ts, storage.ts, theme.ts
│   │   └── ui/           componenti di base scritti a mano (Button, PageHeader, SegmentedControl)
│   └── test/             supporti ai test (setup, matchMedia finto, XHR finto, API activities finta, render dell'app)
└── tests/e2e/            test Playwright (fumo, upload e activities con API simulata, prova sullo stack reale)
```

## Regole (imposte da `pnpm lint` e dai test)

- **Confini:** una feature non importa da un'altra feature né da `app`; `shared` non importa da `features` né da `app`; da fuori una feature si importa solo da `@/features/<nome>` (il suo `index.ts`). Dentro una feature si usano percorsi relativi che non escono dalla feature; per `shared` l'alias `@/shared/...`. Limite: la regola sui percorsi relativi copre fino a 7 livelli di cartelle sotto la feature.
- **Browser:** `localStorage`, `sessionStorage` e `window.open` (anche via `window.`/`globalThis.`) sono vietati ovunque tranne `src/shared/lib/storage.ts`. Unica eccezione documentata: lo script inline di `index.html` (tema senza lampo), che legge la stessa chiave di `THEME_STORAGE_KEY`; un test ne verifica la coincidenza.
- **Testi:** ogni testo dell'interfaccia sta in `src/shared/i18n/locales/{it,en}.json` con le stesse chiavi e gli stessi segnaposto (test di parità). Le chiavi sono tipizzate dall'italiano.
- **Tipi API:** generati, mai a mano; esclusi dal lint.
- **Layout:** `md` (768 px) separa telefono e desktop: barra in basso `md:hidden`, laterale `hidden md:flex`; bersagli di tocco di almeno 44 px (`min-h-11`); altezze con `dvh`; margini sicuri con `env(safe-area-inset-*)`; contenuto `min-w-0`, niente larghezze fisse.

## API e basi URL

`VITE_API_BASE_URL` (predefinita `/api`, senza barra finale; `.env.example`) è la base vista dal browser. Da essa derivano le due basi di `openapi-fetch`:

| Client | Base | Percorsi OpenAPI | Gateway |
|---|---|---|---|
| activities | `<base>` | `/activities`, `/activities/{id}` | `/api/activities` → `/activities` |
| ingestion | `<base>/ingestion` | `/uploads` | `/api/ingestion/uploads` → `/uploads` |

## Formattazione

`shared/lib/format.ts` (con `Intl` nella lingua corrente; hook `useFormatters` in `shared/i18n`): distanza da 1 km in chilometri con 2 decimali, sotto 1 km in metri interi; durata `h:mm:ss` oltre l'ora, altrimenti `m:ss`; dislivello e metriche (bpm, W, rpm) in interi; data e ora nel fuso del browser. Dimensione dei file con unità binarie (`formatFileSize`: «512 B», «1,5 KiB», «32 MiB»). Valori assenti o non validi → `null` (la UI mostra «non disponibile»).

Origine nel PoC e motivazioni: [`ARCHITECTURE/04-frontend.md`](../../ARCHITECTURE/04-frontend.md), [ADR 0005](../../ARCHITECTURE/adr/0005-frontend-react.md), [ADR 0008](../../ARCHITECTURE/adr/0008-frontend-responsivo-con-guscio-nativo.md).
