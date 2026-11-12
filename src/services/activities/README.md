# activities

Archivio canonico delle attività e delle loro serie temporali.

- **Responsabilità:** salvataggio di attività, stream e giri; API di lettura (elenco con filtri, dettaglio, stream ridotti per i grafici); correzioni manuali.
- **Dati posseduti:** schema `activities` (attività, stream come hypertable Timescale, giri).
- **Pubblica:** `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`.
- **Consuma:** `ActivityImported`, `UserDeleted`.
- **Origine nel PoC:** `activities.routes.ts`, tabelle attività in `schema.ts`.
- **Stato:** da implementare.

Vedi [`03-data.md`](../../../ARCHITECTURE/03-data.md).
