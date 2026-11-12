# analytics

Calcolo di metriche, carico, zone, stime fisiologiche e riepiloghi.

- **Responsabilità:** profilo fisiologico con storico; metriche per attività (zone, TSS, record); CTL/ATL/TSB; VO2max, FTP e TDEE stimati; riepiloghi settimanali e mensili; ricalcolo in background quando cambiano le soglie.
- **Dati posseduti:** schema `analytics`.
- **Pubblica:** `MetricsUpdated`, `ThresholdsChanged`.
- **Consuma:** `ActivityStored`, `ActivityUpdated`, `ActivityDeleted`, `UserDeleted`.
- **Origine nel PoC:** `metrics.service.ts`, `metrics.routes.ts`, pagina `Analytics.tsx`.
- **Principio:** le formule sono funzioni pure, testate con dati di riferimento.
- **Stato:** da implementare.

Vedi [`08-analytics.md`](../../../ARCHITECTURE/08-analytics.md).
