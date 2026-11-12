# web

Frontend dell'applicazione: **React + TypeScript + Vite**, installabile come PWA.

- **Stato:** da inizializzare (nessun `package.json` ancora).
- **Organizzazione:** per funzione (`features/activities`, `analytics`, `planning`, `profile`, `auth`) con livello `shared/`. Nessuna importazione diretta tra funzioni.
- **Pagine previste:** Dashboard, Attività, Dettaglio attività, Analisi, Calendario e programmazione, Libreria allenamenti, Upload, Profilo, Collegamenti.
- **Origine nel PoC:** `frontend/src/pages/*`, `components/*`, `hooks/useAuth.ts`.

Dettagli e motivazioni in [`ARCHITECTURE/04-frontend.md`](../../ARCHITECTURE/04-frontend.md) e [ADR 0005](../../ARCHITECTURE/adr/0005-frontend-react.md).
