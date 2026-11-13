# Messaging

Infrastruttura di messaggistica **condivisa** tra i servizi (DEC-001): client NATS, stream JetStream e outbox transazionale.
Solo infrastruttura: nessuna logica di dominio, nessuna dipendenza da servizi o da `ServiceDefaults` (dipende solo da `contracts`).

Il relay dell'outbox e il consumatore con inbox, retry e DLQ si aggiungono in T06 e T07.

## Regole

- Gli eventi si scrivono nell'outbox **nella stessa transazione** dei dati di dominio; solo un relay in background li pubblica su NATS.
- Il servizio **si avvia anche con NATS irraggiungibile** (REQ-013): gli stream si creano in background con tentativi a backoff (250 ms, massimo 5 s). Nessun controllo di NATS nel `ready`.
- Serializzare sempre le buste con `ContractJson.Options`.
- Nei log del componente mai payload, coordinate, token, URL o messaggi di eccezione di rete (solo il tipo dell'errore). Uno stream rifiutato dal server (configurazione incompatibile) è un log `Error` con codice e testo del server; la creazione continua a ritentare con il backoff massimo senza fermare il servizio.

## Client NATS e stream

```csharp
builder.Services.AddNatsMessaging(builder.Configuration);   // legge "Nats:Url" (predefinito nats://127.0.0.1:4222)
```

Registra `INatsConnection` e `INatsJSContext` (singleton, riconnessione automatica), `INatsStreamsReadiness` e il servizio
`NatsStreamsInitializer`, che crea in modo idempotente (`CreateOrUpdate`) gli stream:

| Stream | Subject | Note |
|---|---|---|
| `EVENTS` | `identity.>`, `ingestion.>`, `activities.>`, `analytics.>`, `training-plan.>` | storage su file, finestra di deduplicazione 1 ora, nessun `max_msg_size` (vale `max_payload` del server, 8 MB) |
| `DLQ` | `dlq.>` | storage su file |

`INatsStreamsReadiness.IsReady` / `WhenReadyAsync(ct)` indicano quando gli stream esistono (il relay li attende prima di pubblicare).

- `Nats:Url` è letto alla prima risoluzione della connessione, preferendo l'`IConfiguration` del provider: gli override aggiunti dopo la registrazione (ad esempio nelle `WebApplicationFactory`) sono rispettati.
- Le credenziali NATS **non vanno nell'URL**: usare `AuthOpts`/una configurazione dedicata. Al client è passato l'`ILoggerFactory` del provider (autenticazione, riconnessioni); NATS.Net maschera la password nell'URL ma non username e `host:porta`.
- Lo storage di `EVENTS` e `DLQ` è `File` e **non si può cambiare su uno stream esistente** (il server risponde con errore 10052): serve ricreare lo stream.
- La deduplicazione di JetStream (`Nats-Msg-Id`) protegge **solo entro la finestra di 1 ora**: oltre serve l'idempotenza del consumatore (inbox, T07).

## Outbox

Il `DbContext` del servizio implementa `IOutboxDbContext` e applica la mappatura:

```csharp
public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> o) : DbContext(o), IOutboxDbContext
{
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("ingestion");
        b.ApplyOutbox();          // tabella "outbox" nello schema predefinito, colonne snake_case esplicite
    }
}

services.AddOutbox<IngestionDbContext>();   // IOutbox scoped sul contesto del servizio
```

Il servizio registra `AddNatsMessaging`, `AddOutbox<TDbContext>` e (da T06) `AddOutboxRelay<TDbContext>`. Un servizio con più `DbContext` usa **un solo contesto** per l'outbox.

```csharp
db.Uploads.Add(upload);
outbox.Add(EventNames.ActivityImportedSubject, envelope);   // nel change tracker, nessun SaveChanges
await db.SaveChangesAsync();                                 // una sola transazione
```

`OutboxMessage.Id` = `envelope.Id` (diventa il `Nats-Msg-Id` e garantisce l'idempotenza). Un id duplicato nello stesso contesto lancia già in `Add`
(`InvalidOperationException` del change tracker); con contesti diversi fallisce il `SaveChanges` (violazione di chiave primaria, 23505).
`Add` lancia `ArgumentException` per `Id` vuoto, `Type` vuoto o subject fuori dai prefissi di `EventNames.EventsStreamSubjects`
(non sarebbe mai pubblicabile da alcuno stream).

Tabella `outbox`: `id` uuid PK, `subject` text, `type` text, `payload` bytea, `created_at` timestamptz, `published_at` timestamptz nulla,
`attempts` integer (predefinito 0), `last_error` text nulla; indice parziale `ix_outbox_unpublished` su `created_at` dove `published_at IS NULL`.
Un test ne verifica la forma: le migrazioni dei servizi (T09, T11) devono produrre lo stesso schema.
