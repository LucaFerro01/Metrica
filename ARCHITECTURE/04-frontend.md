# 04 · Frontend

## Stack

| Ambito | Scelta |
|---|---|
| Linguaggio | TypeScript |
| Framework UI | **React** |
| Build | **Vite** |
| Routing | TanStack Router |
| Dati dal server | TanStack Query |
| Componenti | shadcn/ui (basato su Radix) con Tailwind CSS |
| Grafici | Recharts, o visx / ECharts se servono prestazioni maggiori |
| Mappe | MapLibre GL |
| PWA | `vite-plugin-pwa` (Workbox) |
| Gestione pacchetti | pnpm |

Motivazioni in [ADR 0005](adr/0005-frontend-react.md). In sintesi: ecosistema di componenti più ampio e miglior supporto degli strumenti di IA, a scapito di un'impronta leggermente maggiore rispetto a framework come Svelte o Solid.

Next.js non è usato: l'app sta dietro autenticazione e non ha bisogno di rendering lato server; una SPA con Vite è più semplice da capire e da pubblicare come file statici.

## Struttura per funzione

Il codice è organizzato **per area funzionale**, non per tipo di file. Ogni area è un modulo con confini netti.

```text
src/web/
├── src/
│   ├── app/                  Avvio, router, provider, layout
│   ├── features/
│   │   ├── activities/       Elenco, dettaglio, upload
│   │   ├── analytics/        Dashboard, carico, zone, record
│   │   ├── planning/         Calendario, template, piani
│   │   ├── profile/          Profilo atleta, collegamenti
│   │   └── auth/             Accesso, sessione
│   ├── shared/
│   │   ├── ui/               Componenti di base riutilizzabili
│   │   ├── api/              Client generato dall'OpenAPI
│   │   └── lib/              Formattazione, unità, utilità
│   └── main.tsx
├── public/
└── vite.config.ts
```

Regole:

- Una *feature* **non importa** da un'altra feature; condivide solo tramite `shared/`. Si può far rispettare con una regola di lint.
- Ogni feature espone un'interfaccia pubblica (`index.ts`); il resto è interno.
- Il client API è **generato** dalle descrizioni OpenAPI dei servizi, così i tipi seguono il backend.

## Mobile e PWA

- Layout responsivo *mobile-first*; le stesse pagine servono desktop e telefono.
- App installabile con manifest e service worker.
- Offline: **[Ipotesi]** lettura dei dati già visti (elenco e dettaglio recenti) e calendario; la scrittura offline non è un obiettivo iniziale.
- Notifiche push: in seguito (es. promemoria allenamento).

## Prestazioni

- Divisione del codice per rotta (*lazy loading*), grafici e mappe caricati solo dove servono.
- Grafici su dati ridotti dal server (downsampling), mai l'intero stream grezzo.
- Cache di TanStack Query con invalidazione guidata dagli eventi dell'interfaccia; aggiornamenti in tempo reale (es. "analisi in corso" → "pronta") via SSE o polling leggero **[Ipotesi]**.

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

- Unitari e di componente con Vitest e Testing Library.
- End-to-end con Playwright sui flussi principali (collegamento Strava simulato, import, pianificazione).
- Il vecchio test del matching nel PoC (`matching.test.ts`) serve da riferimento per i casi da riprodurre lato server.
