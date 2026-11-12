# ADR 0005 · Frontend React + TypeScript + Vite

- **Stato:** Accettata
- **Data:** 2026-11-12

## Contesto

Serve un frontend leggero e reattivo, utilizzabile su desktop e telefono come PWA. Era stato proposto Svelte 5 per leggerezza e novità. L'autore preferisce un ecosistema di componenti più ampio e un framework che gli strumenti di IA conoscano bene.

## Decisione

**React** con **TypeScript** e **Vite**, come **PWA** (`vite-plugin-pwa`). Routing e dati con **TanStack Router** e **TanStack Query**; componenti con **shadcn/ui** e **Tailwind CSS**; grafici con **Recharts** (o visx/ECharts); mappe con **MapLibre**.

## Motivazioni

- **Ecosistema:** la più ampia scelta di componenti, tabelle, grafici e mappe.
- **Supporto degli strumenti di IA:** React ha la maggior quantità di codice pubblico, quindi suggerimenti più precisi con meno errori.
- **Continuità con il PoC:** già usato (React, Vite, Tailwind, shadcn/ui, Recharts).
- Mercato del lavoro e documentazione abbondanti.

## Conseguenze

- Impronta di codice un po' maggiore rispetto a Svelte o Solid; si compensa con divisione per rotta, caricamento differito e dati ridotti dal server.
- Si evita Next.js: l'app è dietro autenticazione e non ha bisogno di rendering lato server.
- Client API generato dall'OpenAPI dei servizi C#.

## Alternative considerate

- **Svelte 5 / SvelteKit:** molto leggero e piacevole da scrivere, ma ecosistema più piccolo e sintassi recente (rune) meno nota agli strumenti di IA.
- **Vue 3 (+ Nuxt):** valida alternativa, con buone librerie (PrimeVue, Vuetify); ecosistema inferiore a React.
- **SolidJS:** reattività finissima, ecosistema ancora più piccolo.
- **Blazor:** un solo linguaggio (C#), ma peso iniziale e resa su mobile meno adatti a una PWA leggera.
