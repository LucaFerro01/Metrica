# web

Frontend dell'applicazione: **React 19 + TypeScript (strict) + Vite**, con Tailwind CSS 4, TanStack Router e Query, `openapi-fetch` con tipi generati, `i18next`, `lucide-react`. Un'unica app responsiva con due layout (telefono sotto 768 px, desktop da 768 px), predisposta per un guscio nativo Capacitor che si aggiunge dopo lo scheletro (T21); nessuna PWA né service worker (DEC-015, DEC-016).

- **Stato:** scaffold (T16) e pagina Carica (T17, vedi sotto). Le pagine Elenco e Dettaglio sono ancora segnaposto con testo tradotto: il contenuto vero arriva in T18. Gli altri ambiti (analisi, pianificazione, profilo, accesso) non esistono ancora.
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
| `pnpm test:e2e` | Playwright (`tests/e2e/`: `smoke.spec.ts`, `upload.spec.ts`, `real-stack.spec.ts`), ognuno a 375 px e a 1280 px. **Richiede i browser**: `pnpm exec playwright install chromium` (centinaia di MB, una tantum). Avvia (o riusa) il dev server su 5173. Smoke e upload intercettano `/api/*` con `page.route` (solo i percorsi che iniziano con `/api/`, non i moduli di Vite): non servono servizi. `real-stack.spec.ts` si salta salvo `E2E_REAL=1` (vedi *Prova sullo stack reale*). |
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

### Upload in sospeso (per l'elenco, T18)

`src/features/activities/pending/pendingUploads.ts`: store **in memoria** (`useSyncExternalStore`) degli upload accettati (`accepted`, non `duplicate`): `{ externalId, uploadId, acceptedAt }` (`acceptedAt` = `Date.now()` alla ricezione del 202, NON all'avvio dell'invio), **mai il nome del file né il contenuto**. API: `pendingUploads.add / remove(externalId) / clear / getSnapshot / subscribe` e hook `usePendingUploads()`; esportati da `@/features/activities`. Lo stesso `externalId` non si duplica (resta il primo). **Solo in memoria, di proposito:** niente `localStorage`/`sessionStorage` (vietati alla feature dalla regola di lint, e il guscio nativo non li garantisce), quindi ricaricare la pagina azzera lo stato; la fonte di verità resta l'elenco dal server. Chi mostra l'elenco: una riga «in elaborazione» per ogni upload in sospeso il cui `externalId` non è ancora tra le attività, `remove(externalId)` quando compare o allo scadere del timeout «ancora in elaborazione», che parte da `acceptedAt`. Un upload può essere registrato anche se l'utente ha lasciato la pagina Carica prima della risposta.

### Prova sullo stack reale

`tests/e2e/real-stack.spec.ts` (si salta salvo `E2E_REAL=1`, gira solo nel progetto desktop): Vite → gateway → ingestion → NATS → activities, nessuna intercettazione. Carica un GPX di esempio reso unico a ogni esecuzione (UUID nel `creator`): `accepted`, l'attività arriva in `activities` (il totale di `GET /api/activities` cresce), di nuovo → `duplicate`; un GPX non ben formato → 422 con messaggio tradotto. Ogni esecuzione aggiunge un'attività al database di sviluppo. Con lo stack avviato (`docker compose -f deploy/compose.yaml up -d`, poi i tre servizi con `dotnet run --no-build --project <csproj>` uno alla volta, poi `pnpm dev`):

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
│   │   └── activities/   upload/, pending/ (upload in sospeso), list/, detail/ + index.ts (interfaccia pubblica)
│   ├── shared/
│   │   ├── api/          config.ts (base URL), client.ts, tipi generati *.d.ts (committati)
│   │   ├── i18n/         init, locales/it.json e en.json
│   │   ├── lib/          format.ts, storage.ts, theme.ts
│   │   └── ui/           componenti di base scritti a mano (Button, PageHeader, SegmentedControl)
│   └── test/             supporti ai test (setup, matchMedia finto, XHR finto, render dell'app)
└── tests/e2e/            test Playwright (fumo, upload con API simulata, prova sullo stack reale)
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
