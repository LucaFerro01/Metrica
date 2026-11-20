# contracts

Definizioni **condivise** tra i servizi: buste e payload degli eventi, e poco altro.

Regole:

- Solo **definizioni** (tipi, schemi): nessuna logica, nessun accesso a dati o rete.
- Un evento si può solo **estendere** con campi opzionali; un cambio incompatibile richiede una nuova `version`.
- È l'unico codice che più servizi possono condividere.

Formato: record C# in `Metrica.Contracts` (DEC-005), serializzati con `ContractJson.Options` (camelCase, enum come stringhe minuscole, `null` espliciti). Tipi: `EventEnvelope<T>`, `ActivityImported` (versione 1) con `ActivitySummary`, `LapData` e `StreamColumns` (stream a colonne), `Sport`, `EventNames` (nomi di evento, subject e stream `EVENTS`/`DLQ`), `StreamLimits` (massimo 100.000 punti). L'arrotondamento dei valori dello stream (DEC-012) è un obbligo del produttore (ingestion), non applicato dal contratto. Busta e convenzioni in [`ARCHITECTURE/02-communication.md`](../../../ARCHITECTURE/02-communication.md).

## Eventi

Solo `ActivityImported` (versione 1) è **implementato**; gli altri sono **previsti** e non hanno ancora una definizione nel progetto.

| Evento | Pubblicato da | Stato |
|---|---|---|
| `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted` | identity | Previsto |
| `ActivityImported` | ingestion | **Implementato** (subject `ingestion.activity.imported`, consumato da activities) |
| `ActivityStored`, `ActivityUpdated`, `ActivityDeleted` | activities | Previsto |
| `MetricsUpdated`, `ThresholdsChanged` | analytics | Previsto |
| `WorkoutMatched`, `PlanChanged` | training-plan | Previsto |

## Test

`dotnet test src/shared/contracts/tests/Metrica.Contracts.Tests` (senza contenitori): serializzazione con `ContractJson.Options`, nomi di eventi e subject, e il caso peggiore di 100.000 punti con tutte le colonne, che deve restare sotto 8 MiB.
