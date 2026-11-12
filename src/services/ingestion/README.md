# ingestion

Raccolta delle attività dalle fonti esterne e traduzione nel modello comune.

- **Responsabilità:** connettori `Strava`, `Zepp`, `FitGpx`; webhook (risposta immediata, elaborazione in background); import storico a ritmo controllato; parsing di FIT e GPX; deduplicazione.
- **Dati posseduti:** schema `ingestion` (stato di sincronizzazione, chiavi di deduplicazione, coda di lavoro, file grezzi).
- **Pubblica:** `ActivityImported`.
- **Consuma:** `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted`.
- **Origine nel PoC:** `strava.service.ts`, `zepp.service.ts`, `fitgpx.service.ts`, `strava.worker.ts`, `webhook.routes.ts`.
- **Stato:** da implementare.

Vedi [`07-integrations.md`](../../../ARCHITECTURE/07-integrations.md).
