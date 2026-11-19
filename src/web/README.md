# web

Frontend dell'applicazione: **React 19 + TypeScript (strict) + Vite**, con Tailwind CSS 4, TanStack Router e Query, `openapi-fetch` con tipi generati, `i18next`, `lucide-react`. Un'unica app responsiva con due layout (telefono sotto 768 px, desktop da 768 px), predisposta per un guscio nativo Capacitor che si aggiunge dopo lo scheletro (T21); nessuna PWA né service worker (DEC-015, DEC-016).

- **Stato:** scaffold (T16). Le pagine Elenco, Dettaglio e Carica sono segnaposto con testo tradotto: il contenuto vero arriva in T17 (upload) e T18 (elenco e dettaglio). Gli altri ambiti (analisi, pianificazione, profilo, accesso) non esistono ancora.
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
| `pnpm test:e2e` | Playwright, test di fumo in `tests/e2e/`. **Richiede i browser**: `pnpm exec playwright install chromium` (centinaia di MB, una tantum). Intercetta `/api/*` con `page.route`: non servono servizi. |
| `pnpm gen:api` | Rigenera `src/shared/api/ingestion.d.ts` e `activities.d.ts` dai documenti OpenAPI di `ingestion` (`:5081`) e `activities` (`:5082`). Servono i due servizi avviati in `Development`. Da eseguire quando cambiano le API, poi rivedere il diff. |

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
│   │   └── activities/   upload/, list/, detail/ + index.ts (interfaccia pubblica)
│   ├── shared/
│   │   ├── api/          config.ts (base URL), client.ts, tipi generati *.d.ts (committati)
│   │   ├── i18n/         init, locales/it.json e en.json
│   │   ├── lib/          format.ts, storage.ts, theme.ts
│   │   └── ui/           componenti di base scritti a mano (Button, PageHeader, SegmentedControl)
│   └── test/             supporti ai test (setup, matchMedia finto, render dell'app)
└── tests/e2e/            test Playwright di fumo
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

`shared/lib/format.ts` (con `Intl` nella lingua corrente; hook `useFormatters` in `shared/i18n`): distanza da 1 km in chilometri con 2 decimali, sotto 1 km in metri interi; durata `h:mm:ss` oltre l'ora, altrimenti `m:ss`; dislivello e metriche (bpm, W, rpm) in interi; data e ora nel fuso del browser. Valori assenti o non validi → `null` (la UI mostra «non disponibile»).

Origine nel PoC e motivazioni: [`ARCHITECTURE/04-frontend.md`](../../ARCHITECTURE/04-frontend.md), [ADR 0005](../../ARCHITECTURE/adr/0005-frontend-react.md), [ADR 0008](../../ARCHITECTURE/adr/0008-frontend-responsivo-con-guscio-nativo.md).
