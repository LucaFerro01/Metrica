# Messaging

Infrastruttura di messaggistica **condivisa** tra i servizi (DEC-001): client NATS, stream JetStream e outbox transazionale.
Solo infrastruttura: nessuna logica di dominio, nessuna dipendenza da servizi o da `ServiceDefaults` (dipende solo da `contracts`).

Il relay dell'outbox (T06) è descritto sotto; il consumatore con inbox, retry e DLQ si aggiunge in T07.

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

Il servizio registra `AddNatsMessaging`, `AddOutbox<TDbContext>` e `AddOutboxRelay<TDbContext>`. Un servizio con più `DbContext` usa **un solo contesto** per l'outbox.

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

## Relay dell'outbox

```csharp
services.AddNatsMessaging(builder.Configuration);
services.AddOutbox<IngestionDbContext>();
services.AddOutboxRelay<IngestionDbContext>();   // BackgroundService + OutboxRelayStats (singleton)
```

`OutboxRelay<TDbContext>` pubblica su JetStream i messaggi non pubblicati dell'outbox e li marca (`published_at`).
Opzioni (sezione `Outbox`, validate all'avvio: ogni valore deve essere almeno 1):

| Chiave | Predefinito | Significato |
|---|---|---|
| `Outbox:PollIntervalMs` | 500 | pausa tra due letture con coda vuota (e base del backoff) |
| `Outbox:BatchSize` | 50 | messaggi per transazione |
| `Outbox:PublishTimeoutSeconds` | 10 | attesa massima dell'ack per un messaggio |
| `Outbox:MaxBackoffSeconds` | 30 | pausa massima del backoff |

Come lavora:

- **Avvio non bloccante.** `ExecuteAsync` cede subito il controllo e attende `INatsStreamsReadiness.WhenReadyAsync`: con NATS fermo il servizio parte e il relay aspetta in background (REQ-013). `IOutbox.Add` + `SaveChanges` non richiedono né il relay né NATS.
- **Più istanze, nessun doppio lavoro.** Ogni ciclo apre una transazione e legge fino a `BatchSize` righe non pubblicate in ordine di `created_at` con `SELECT ... FOR UPDATE SKIP LOCKED` (nome di tabella, schema e colonne ricavati dal modello EF): un'altra istanza salta le righe bloccate invece di attenderle. Il lock dura quanto il batch.
- **Pubblicazione.** Per ogni messaggio, in ordine: `Payload` (byte grezzi) sul `Subject` con `Nats-Msg-Id` = `Id`, attesa dell'ack (`PublishTimeoutSeconds`), poi `PublishedAt = UtcNow`. Un ack con `Duplicate = true` (stesso `Nats-Msg-Id` già accettato nella finestra di deduplicazione: crash tra pubblicazione e marcatura) è un **successo** e incrementa `OutboxRelayStats.Duplicates`. Alla fine del batch `SaveChanges` e commit in una sola transazione.
- **Errori per messaggio.** `Attempts++` e `LastError` = solo il nome del tipo dell'eccezione (per gli errori del server anche `(codice/errCode)`): mai payload, subject, URL o testo dell'eccezione. Il messaggio resta non pubblicato e il relay passa al successivo (un messaggio problematico, ad esempio con un subject senza stream, non blocca gli altri). Il batch si interrompe **solo** se il bus non risponde (timeout del relay, errori di rete/socket/IO, eccezioni di connessione o di timeout di NATS.Net): gli altri messaggi farebbero la stessa fine tenendo il lock per un timeout ciascuno. **Qualsiasi altro errore** (risposta del server, stream assente, payload oltre `max_payload`, ...) riguarda quel messaggio e non ferma la coda. `LastError` non è azzerato quando un tentativo successivo riesce (resta la storia dell'ultimo errore insieme ad `Attempts`).
- **Backoff.** Se un ciclo non pubblica nulla per errori (NATS giù) la pausa raddoppia a ogni ciclo a partire da `PollIntervalMs` (la prima pausa dopo un fallimento è già 2×`PollIntervalMs`) fino a `MaxBackoffSeconds` e torna a `PollIntervalMs` al primo ciclo riuscito. Con coda vuota la pausa è `PollIntervalMs`; con un batch pieno e senza errori si rilegge subito. Con errori parziali (qualche messaggio fallito, altri pubblicati) si attende `PollIntervalMs`.
- **Errori del database** (connessione persa, ecc.) non fanno terminare il servizio: warning con solo il tipo dell'eccezione e nuovo tentativo con lo stesso backoff. Gli stessi warning identici sono limitati a uno ogni 10 secondi.
- **Arresto.** La cancellazione durante una pubblicazione chiude la transazione (nessun lock appeso): il messaggio in volo resta non pubblicato e lo riprende un'altra istanza; il salvataggio finale delle marcature (`SaveChanges` + commit) usa un token indipendente dall'arresto con timeout di 5 secondi, così un arresto non fa perdere un batch già pubblicato; se il salvataggio fallisce i messaggi saranno ripubblicati e confermati come duplicati. I contatori `Published`/`Duplicates` crescono solo dopo il commit.
- **Strategia di retry di EF.** Se il contesto ha `EnableRetryOnFailure` il batch gira dentro la strategia (stato del change tracker azzerato a ogni tentativo).

`OutboxRelayStats` espone contatori thread-safe per diagnostica e test: `Published` (messaggi marcati, inclusi i duplicati), `Duplicates` (sottoinsieme con ack duplicato), `Failures` (pubblicazioni fallite), `Cycles` (cicli senza errori del database).

Garanzie e limiti:

- La consegna è **at-least-once** con deduplicazione lato server: entro la finestra di 1 ora dello stream `EVENTS` un crash tra pubblicazione e marcatura non produce doppioni (il `Nats-Msg-Id` li scarta). **Oltre la finestra** lo stesso messaggio potrebbe essere memorizzato due volte (ad esempio dopo un'interruzione molto lunga tra pubblicazione e marcatura): serve l'idempotenza del consumatore (inbox, T07).
- L'ordine di pubblicazione segue `created_at` **all'interno di un'istanza**; con più istanze i batch si distribuiscono e l'ordine globale non è garantito (i consumatori non devono dipenderne).
- Non c'è un parcheggio automatico dei messaggi che falliscono sempre: restano nell'outbox con `Attempts` crescente e `LastError` e sono ritentati a ogni ciclo; l'operatore li individua da quelle colonne.
- Un messaggio oltre `max_payload` del server (8 MB) non è pubblicabile (`NatsPayloadTooLargeException`): resta nell'outbox con `Attempts` crescente e `LastError` e **non blocca** gli altri (test di regressione con 9 MB seguito da messaggi buoni). Il limite va comunque rispettato a monte (`ingestion` rifiuta file con più di 100.000 punti, DEC-012).
- Con NATS giù `Attempts` cresce di 1 per ciclo e solo per il primo messaggio del batch (il batch si interrompe al primo timeout). Il lock di riga e la connessione al database restano occupati fino a `PublishTimeoutSeconds` (10 s predefiniti) a ogni ciclo.
- **Messaggi problematici in testa alla coda.** Il relay legge sempre i più vecchi: se `BatchSize` (50) messaggi problematici sono i più vecchi, occupano ogni batch e **affamano** la coda. Non c'è parcheggio automatico: il backlog di `Attempts` va monitorato (ad esempio `SELECT count(*) FROM outbox WHERE published_at IS NULL AND attempts > 0`) e i messaggi vanno corretti o rimossi a mano.
- **Memoria.** Un batch carica in memoria i payload di tutte le righe lette: 50 messaggi da 6-8 MB occupano circa 300 MB (più le copie per la pubblicazione). Con payload grandi (eventi con stream completo, DEC-012) valutare un `Outbox:BatchSize` più basso.
- Il deploy non imposta `idle_in_transaction_session_timeout`: la transazione del relay è inattiva mentre attende l'ack, quindi un timeout basso a livello di server la interromperebbe; chiude comunque a ogni arresto e dopo `PublishTimeoutSeconds`.
