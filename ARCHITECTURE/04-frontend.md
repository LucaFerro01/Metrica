# 04 · Frontend

## Stack

| Ambito | Scelta | Stato nello scheletro |
|---|---|---|
| Linguaggio | TypeScript | In uso (5.9, `strict`) |
| Framework UI | **React** | In uso (19) |
| Build | **Vite** | In uso (8) |
| Routing | TanStack Router | In uso |
| Dati dal server | TanStack Query | In uso |
| Componenti | shadcn/ui (basato su Radix) con Tailwind CSS | Tailwind 4 in uso; componenti **scritti a mano** in `shared/ui` (DEC-016); shadcn/ui rimandato a quando arrivano calendario ed editor |
| Client API | Generato dall'OpenAPI dei servizi | In uso: `openapi-typescript` e `openapi-fetch`, tipi committati (`pnpm gen:api`) |
| Lingue e icone | — | In uso: `i18next` e `react-i18next` (italiano e inglese), `lucide-react` (DEC-016) |
| Grafici | Recharts, o visx / ECharts se servono prestazioni maggiori | **Previsto**, non presente |
| Mappe | MapLibre GL | **Previsto**, non presente |
| PWA | `vite-plugin-pwa` (Workbox) | **Previsto**, non presente (nessun service worker) |
| Gestione pacchetti | pnpm | In uso (12, fissato in `packageManager`) |
| Test | Vitest, Testing Library, Playwright | In uso, vedi *Test* |

Motivazioni in [ADR 0005](adr/0005-frontend-react.md). In sintesi: ecosistema di componenti più ampio e miglior supporto degli strumenti di IA, a scapito di un'impronta leggermente maggiore rispetto a framework come Svelte o Solid.

## Stato dello scheletro

L'app dialoga solo con il gateway (`/api`, inoltrato dal dev server di Vite a `127.0.0.1:5080`, nessun CORS) e ha tre pagine ([README di `src/web`](../src/web/README.md)):

| Pagina | Percorso | Contenuto |
|---|---|---|
| Carica | `/upload` | Un file `.gpx`/`.fit`, controllo dei 32 MiB **prima** dell'invio (il 413 di un file grande attraverso il gateway non è leggibile dal browser), avanzamento con `XMLHttpRequest`, annullamento, esiti `accepted`/`duplicate` ed errori tradotti |
| Elenco | `/activities?sport=&page=` | Schede sotto 768 px e tabella da 768 px, filtro per sport, paginazione (20 per pagina), stati di caricamento, vuoto ed errore, righe «in elaborazione» per i file appena caricati |
| Dettaglio | `/activities/$activityId` | Riepilogo, valori di base e giri; «non disponibile» per i campi assenti, 404 per un id inesistente o di un altro utente |

`/` riporta all'elenco. **Righe «in elaborazione»:** gli upload accettati (202) si ricordano **in memoria** (`pendingUploads`, mai in `localStorage`); per ciascuno una query `GET /activities?externalId=<id>&pageSize=1` ogni 2 secondi, sospesa a scheda nascosta, con timeout di 60 secondi dall'accettazione e un ultimo controllo alla scadenza (DEC-008, DEC-017). Le righe si mostrano **solo a pagina 1 e senza filtro per sport** (il rilevamento prosegue comunque). Vale solo per i file caricati nella sessione del browser: dopo un ricaricamento o da un altro dispositivo l'elenco non si aggiorna da solo.

Realizzato anche: tema chiaro/scuro con interruttore a tre posizioni (Sistema, Chiaro, Scuro) applicato prima del primo disegno e salvato tramite `shared/lib/storage`; italiano e inglese con test di parità delle chiavi (REQ-039); layout a due modalità con bersagli da 44 px, margini sicuri e `viewport-fit=cover` (DEC-015, REQ-037, REQ-038); base URL dell'API da `VITE_API_BASE_URL` (predefinito `/api`); regole di lint di architettura (vedi *Struttura per funzione*).

**Ancora previsto** (non esiste nel codice): Dashboard, Analisi, Calendario e programmazione, Libreria allenamenti, Profilo, Collegamenti, accesso (login), PWA e offline, notifiche, mappe e grafici (oggi il dettaglio non mostra lo stream: chiede `maxPoints=2`), guscio nativo Capacitor (T21).

Next.js non è usato: l'app sta dietro autenticazione e non ha bisogno di rendering lato server; una SPA con Vite è più semplice da capire e da pubblicare come file statici.

## Struttura per funzione

Il codice è organizzato **per area funzionale**, non per tipo di file. Ogni area è un modulo con confini netti. Disegno completo (le cartelle marcate *previsto* non esistono ancora):

```text
src/web/
├── src/
│   ├── app/                  Avvio, router, provider, layout
│   ├── features/
│   │   ├── activities/       Elenco, dettaglio, upload (esiste: upload/, pending/, data/, display/, list/, detail/)
│   │   ├── analytics/        Dashboard, carico, zone, record (previsto)
│   │   ├── planning/         Calendario, template, piani (previsto)
│   │   ├── profile/          Profilo atleta, collegamenti (previsto)
│   │   └── auth/             Accesso, sessione (previsto)
│   ├── shared/
│   │   ├── ui/               Componenti di base riutilizzabili (scritti a mano)
│   │   ├── api/              Client generato dall'OpenAPI (tipi committati)
│   │   ├── i18n/             Italiano e inglese
│   │   └── lib/              Formattazione, storage, tema (unità: previste)
│   ├── test/                 Supporti ai test
│   └── main.tsx
├── tests/e2e/                Test Playwright
├── public/                   (previsto: non esiste ancora)
└── vite.config.ts
```

Regole (le prime due **imposte da `pnpm lint`**, con test che le provano con la vera API di ESLint):

- Una *feature* **non importa** da un'altra feature né da `app`; condivide solo tramite `shared/`, che a sua volta non importa da `features` né da `app`.
- Ogni feature espone un'interfaccia pubblica (`index.ts`); da fuori si importa solo da lì, il resto è interno.
- `localStorage`, `sessionStorage` e `window.open` sono vietati fuori da `shared/lib/storage.ts` (unica eccezione: lo script inline di `index.html` per il tema, con un test che verifica che la chiave coincida), perché il guscio nativo non li garantisce (DEC-015).
- Il client API è **generato** dalle descrizioni OpenAPI dei servizi (`ingestion` per l'upload, `activities` per elenco e dettaglio), così i tipi seguono il backend; non si scrive a mano.
- Ogni testo sta in `shared/i18n/locales/{it,en}.json` con le stesse chiavi (test di parità).

## Mobile, desktop e guscio nativo

Una sola applicazione React, con **due modalità di layout** sulle stesse pagine e sugli stessi percorsi ([ADR 0008](adr/0008-frontend-responsivo-con-guscio-nativo.md)):

| | Telefono (sotto 768 px) | Desktop (da 768 px) |
|---|---|---|
| Chi lo usa | L'atleta | Chi pianifica (allenatore o atleta) e l'atleta per l'analisi |
| Navigazione | Barra in basso | Barra laterale |
| Elenco attività | Schede | Tabella |
| Azioni frequenti | Allenamento di oggi, attività appena fatta, caricare un file | Calendario, libreria e editor degli allenamenti, analisi |
| Piano di allenamento | Sola lettura | Modifica |

- Bersagli di tocco di almeno 44 px sul telefono; margini sicuri dello schermo (`viewport-fit=cover` e `env(safe-area-inset-*)`).
- **Predisposto per un guscio nativo** (Capacitor): base URL dell'API configurabile in build (`VITE_API_BASE_URL`), nessuna dipendenza da funzioni solo da browser (popup, download via link, accesso diretto a `localStorage` fuori da `shared/lib/storage`), selezione file con `<input type="file">`, instradamento servito dalla radice. Il guscio si aggiunge dopo lo scheletro del sistema (task T21), con l'account Apple Developer e una compilazione iOS su macchine macOS nel cloud per l'iPhone; l'Android si compila da Windows. Il gateway dovrà ammettere le origini del guscio (CORS).
- **PWA installabile**: **[Ipotesi]** in seguito, con `vite-plugin-pwa`; non fa parte dello scheletro. Su iPhone le notifiche push web funzionano solo ad app aggiunta alla schermata Home da Safari.
- Offline: **[Ipotesi]** lettura dei dati già visti (elenco e dettaglio recenti) e calendario; la scrittura offline non è un obiettivo iniziale.
- Notifiche push: in seguito (es. promemoria allenamento), dal guscio nativo o dalla PWA.
- Ruolo allenatore: il modello allenatore/atleta (chi pianifica per chi) non è deciso; cambia i confini di `identity` e `training-plan` (vedi [README](README.md#decisioni-aperte)).

## Prestazioni

- Divisione del codice per rotta (*lazy loading*), grafici e mappe caricati solo dove servono. **Previsto**: oggi le rotte sono importate in modo statico (un solo bundle).
- Grafici su dati ridotti dal server (downsampling), mai l'intero stream grezzo: il server lo supporta già (`maxPoints`), i grafici non esistono ancora.
- Cache di TanStack Query con invalidazione guidata dagli eventi dell'interfaccia. Aggiornamenti in tempo reale: oggi **polling a 2 secondi** solo mentre c'è un caricamento in elaborazione (DEC-008); SSE (es. "analisi in corso" → "pronta") è un'**[Ipotesi]** rimandata.

## Micro-frontend: sì o no?

**Decisione provvisoria: no, non all'inizio** ([ADR 0007](adr/0007-micro-frontend.md)).

I micro-frontend dividono la UI in applicazioni distribuite e rilasciate in modo indipendente (es. con Module Federation o web component). Il vantaggio principale è **organizzativo**: team diversi che rilasciano senza coordinarsi. Il costo è reale: dipendenze condivise da allineare, coerenza visiva e di navigazione, più complessità di build, test end-to-end più difficili, prestazioni peggiori se non curati.

| | Una sola app modulare | Micro-frontend |
|---|---|---|
| Sviluppatori | Uno o pochi | Più team autonomi |
| Rilasci indipendenti | Non necessari | Necessari |
| Complessità | Bassa | Alta |
| Coerenza UI | Facile | Da presidiare |
| Valore didattico | Basso su questo tema | Alto |

Con un solo sviluppatore il vantaggio organizzativo non esiste. La struttura per funzione sopra rende però la scissione futura poco costosa: ogni `feature` ha già confini netti e comunica solo tramite `shared/`.

**Quando rivalutare:** se più persone lavorano sulla UI, se una parte (es. l'editor di allenamenti) ha un ciclo di rilascio molto diverso, o se si decide di farne un **esperimento di apprendimento** a sé, isolando una sola area.

## Test

- Unitari e di componente con Vitest e Testing Library (jsdom): `pnpm test`.
- End-to-end con Playwright (`pnpm test:e2e`), ogni test a 375 px e a 1280 px, con le chiamate `/api/*` intercettate (caricamento, elenco, dettaglio, smoke del layout). `real-stack.spec.ts` prova invece lo stack vero (Vite, gateway, ingestion, NATS, activities), si salta salvo `E2E_REAL=1` e gira **solo a 1280 px** (si salta con una larghezza sotto 768 px); ogni esecuzione aggiunge attività al database di sviluppo. Solo Chromium è provato. *Previsti, non presenti:* collegamento Strava simulato e pianificazione.
- Il vecchio test del matching nel PoC (`matching.test.ts`) serve da riferimento per i casi da riprodurre lato server.
