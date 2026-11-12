# contracts

Definizioni **condivise** tra i servizi: buste e payload degli eventi, e poco altro.

Regole:

- Solo **definizioni** (tipi, schemi): nessuna logica, nessun accesso a dati o rete.
- Un evento si può solo **estendere** con campi opzionali; un cambio incompatibile richiede una nuova `version`.
- È l'unico codice che più servizi possono condividere.

Formato (C# o schema JSON, o entrambi con generazione) da decidere con il primo servizio. Busta e convenzioni in [`ARCHITECTURE/02-communication.md`](../../../ARCHITECTURE/02-communication.md).

## Eventi previsti

| Evento | Pubblicato da |
|---|---|
| `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` | identity |
| `ActivityImported` | ingestion |
| `ActivityStored`, `ActivityUpdated`, `ActivityDeleted` | activities |
| `MetricsUpdated`, `ThresholdsChanged` | analytics |
| `WorkoutMatched`, `PlanChanged` | training-plan |
