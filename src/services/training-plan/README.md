# training-plan

Programmazione degli allenamenti e confronto con quanto svolto.

- **Responsabilità:** libreria di template, allenamenti a step, calendario, piani su più settimane, matching tra allenamento pianificato e attività svolta con indicatore di aderenza.
- **Dati posseduti:** schema `training_plan`.
- **Pubblica:** `WorkoutMatched`, `PlanChanged`.
- **Consuma:** `ActivityStored`, `ThresholdsChanged`, `UserDeleted`.
- **Origine nel PoC:** `scheduling.routes.ts`, migrazioni `scheduled_workouts`, `workout_steps`, `workout_templates_library`, `pages/scheduling/matching.ts` (e il suo test, da usare come specifica).
- **Stato:** previsto, non implementato: nessun progetto .NET, nessuna migrazione, nessun evento definito in `contracts`. Esistono solo questo README e due predisposizioni comuni: `deploy/postgres/init/01-schemas.sql` crea già il ruolo `training_plan_svc` e lo schema `training_plan`, e `EventNames.EventsStreamSubjects` riserva il prefisso `training-plan.>` nello stream `EVENTS`.
