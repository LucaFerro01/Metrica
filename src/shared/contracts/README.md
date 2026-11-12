# contracts

Definizioni **condivise** tra i servizi: buste e payload degli eventi, e poco altro.

Regole:

- Solo **definizioni** (tipi, schemi): nessuna logica, nessun accesso a dati o rete.
- Un evento si può solo **estendere** con campi opzionali; un cambio incompatibile richiede una nuova `version`.
- È l'unico codice che più servizi possono condividere.

Formato: record C# in `TriMetric.Contracts` (DEC-005), serializzati con `ContractJson.Options` (camelCase, enum come stringhe minuscole, `null` espliciti). Tipi: `EventEnvelope<T>`, `ActivityImported` (versione 1) con `ActivitySummary`, `LapData` e `StreamColumns` (stream a colonne), `Sport`, `EventNames` (nomi di evento, subject e stream `EVENTS`/`DLQ`), `StreamLimits` (massimo 100.000 punti). L'arrotondamento dei valori dello stream (DEC-012) è un obbligo del produttore (ingestion), non applicato dal contratto. Busta e convenzioni in [`ARCHITECTURE/02-communication.md`](../../../ARCHITECTURE/02-communication.md).

## Eventi previsti

| Evento | Pubblicato da |
|---|---|
| `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` | identity |
| `ActivityImported` | ingestion |
| `ActivityStored`, `ActivityUpdated`, `ActivityDeleted` | activities |
| `MetricsUpdated`, `ThresholdsChanged` | analytics |
| `WorkoutMatched`, `PlanChanged` | training-plan |
