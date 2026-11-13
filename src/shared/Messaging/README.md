# Messaging

Infrastruttura di messaggistica **condivisa** tra i servizi (DEC-001): client NATS, stream JetStream e outbox transazionale.
Solo infrastruttura: nessuna logica di dominio, nessuna dipendenza da servizi o da `ServiceDefaults` (dipende solo da `contracts`).

Il relay dell'outbox (T06) e il consumatore con inbox, retry e DLQ (T07) sono descritti sotto.

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
- La deduplicazione di JetStream (`Nats-Msg-Id`) protegge **solo entro la finestra di 1 ora**: oltre serve l'idempotenza del consumatore (inbox, vedi *Consumatore idempotente*).

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

- La consegna è **at-least-once** con deduplicazione lato server: entro la finestra di 1 ora dello stream `EVENTS` un crash tra pubblicazione e marcatura non produce doppioni (il `Nats-Msg-Id` li scarta). **Oltre la finestra** lo stesso messaggio potrebbe essere memorizzato due volte (ad esempio dopo un'interruzione molto lunga tra pubblicazione e marcatura): serve l'idempotenza del consumatore (inbox, vedi *Consumatore idempotente*).
- L'ordine di pubblicazione segue `created_at` **all'interno di un'istanza**; con più istanze i batch si distribuiscono e l'ordine globale non è garantito (i consumatori non devono dipenderne).
- Non c'è un parcheggio automatico dei messaggi che falliscono sempre: restano nell'outbox con `Attempts` crescente e `LastError` e sono ritentati a ogni ciclo; l'operatore li individua da quelle colonne.
- Un messaggio oltre `max_payload` del server (8 MB) non è pubblicabile (`NatsPayloadTooLargeException`): resta nell'outbox con `Attempts` crescente e `LastError` e **non blocca** gli altri (test di regressione con 9 MB seguito da messaggi buoni). Il limite va comunque rispettato a monte (`ingestion` rifiuta file con più di 100.000 punti, DEC-012).
- Con NATS giù `Attempts` cresce di 1 per ciclo e solo per il primo messaggio del batch (il batch si interrompe al primo timeout). Il lock di riga e la connessione al database restano occupati fino a `PublishTimeoutSeconds` (10 s predefiniti) a ogni ciclo.
- **Messaggi problematici in testa alla coda.** Il relay legge sempre i più vecchi: se `BatchSize` (50) messaggi problematici sono i più vecchi, occupano ogni batch e **affamano** la coda. Non c'è parcheggio automatico: il backlog di `Attempts` va monitorato (ad esempio `SELECT count(*) FROM outbox WHERE published_at IS NULL AND attempts > 0`) e i messaggi vanno corretti o rimossi a mano.
- **Memoria.** Un batch carica in memoria i payload di tutte le righe lette: 50 messaggi da 6-8 MB occupano circa 300 MB (più le copie per la pubblicazione). Con payload grandi (eventi con stream completo, DEC-012) valutare un `Outbox:BatchSize` più basso.
- Il deploy non imposta `idle_in_transaction_session_timeout`: la transazione del relay è inattiva mentre attende l'ack, quindi un timeout basso a livello di server la interromperebbe; chiude comunque a ogni arresto e dopo `PublishTimeoutSeconds`.

## Consumatore idempotente (inbox, retry, DLQ)

Il `DbContext` del servizio che consuma implementa `IInboxDbContext` e applica la mappatura:

```csharp
public sealed class ActivitiesDbContext(DbContextOptions<ActivitiesDbContext> o) : DbContext(o), IInboxDbContext
{
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("activities");
        b.ApplyInbox();           // tabella "inbox" nello schema predefinito, colonne snake_case esplicite
    }
}

public sealed class ActivityImportedHandler(ActivitiesDbContext db) : IEventHandler<ActivityImported>
{
    public async Task HandleAsync(EventEnvelope<ActivityImported> envelope, CancellationToken ct)
    {
        // Stesso DbContext, stessa transazione dell'inbox: nessun Commit, nessuna transazione propria.
        // EventValidationException => direttamente in DLQ, senza ritentare.
    }
}

services.AddEventConsumer<ActivitiesDbContext, ActivityImported, ActivityImportedHandler>(new ConsumerOptions
{
    FilterSubject = EventNames.ActivityImportedSubject,
    DurableName = "activities-activity-imported",      // senza punti
});
```

Richiede `AddNatsMessaging` e il `DbContext` registrato. `THandler` è registrato **scoped** e, per ogni messaggio, risolto in uno scope nuovo insieme al
`TDbContext` dell'inbox: il gestore e l'inbox condividono contesto, connessione e transazione. `ConsumerOptions` è validata subito
(`ArgumentException`), quindi all'avvio dell'applicazione e senza bisogno della rete.

| Opzione | Predefinito | Significato |
|---|---|---|
| `Stream` | `EVENTS` | stream da cui si consuma |
| `FilterSubject` | obbligatorio | subject filtrato, ad esempio `ingestion.activity.imported` |
| `DurableName` | obbligatorio | nome del consumatore durevole, **senza punti**: è anche il subject DLQ `dlq.<DurableName>` e il valore della colonna `consumer` dell'inbox |
| `MaxDeliver` | 5 | tentativi totali, il primo incluso |
| `Backoff` | 1 s, 2 s, 5 s, 10 s | attesa prima del nuovo tentativo: elemento `min(consegne - 1, lunghezza - 1)` |
| `AckWait` | 60 s | scadenza dell'ack per JetStream (il consumatore invia `AckProgress` ogni circa `AckWait / 3`) |
| `MaxConcurrency` | 1 | messaggi in parallelo; è anche il `MaxAckPending` del consumatore JetStream |

Tabella `inbox`: `message_id` uuid, `consumer` text, `processed_at` timestamptz (tutte non nulle), **chiave primaria composta `(consumer, message_id)`**, nessun altro indice.
Un test ne verifica la forma: la migrazione di `activities` (T11) deve produrre lo stesso schema.

Come lavora, per ogni messaggio:

1. **Poison pill.** Se `NumDelivered > MaxDeliver` (il processo è morto più volte durante l'elaborazione) va in DLQ con motivo `MaxDeliverExceeded` e `Term`, senza eseguire il gestore.
2. **Malformato.** Payload non deserializzabile, `Id` vuoto o `Data` nulla: in DLQ al primo tentativo (motivo `Malformed`), nessun retry.
3. **Una sola transazione** (scope nuovo): `INSERT ... ON CONFLICT DO NOTHING` nell'inbox (SQL costruito dai metadati del modello EF), poi il gestore, poi `SaveChanges` se restano modifiche pendenti, poi commit. Se l'inserimento non scrive righe l'evento è un **duplicato**: il gestore non gira e il messaggio è confermato (`Ack`). **`Ack` solo dopo il commit.**
4. **`EventValidationException`** dal gestore: rollback, DLQ (motivo `Validation`), `Term`, nessun retry.
5. **Altra eccezione** (gestore, `SaveChanges`, database, commit): rollback (la riga di inbox non resta) e `NakWithDelay` con il backoff; all'ultimo tentativo (`NumDelivered >= MaxDeliver`) va in DLQ con motivo uguale al tipo dell'eccezione (per gli errori del server NATS anche `(codice/errCode)`) e `Term`.
6. **AckProgress** periodico durante l'elaborazione, così un'elaborazione più lunga di `AckWait` non causa riconsegne.

**DLQ.** Il messaggio originale (payload invariato) è pubblicato sullo stream `DLQ` al subject `dlq.<DurableName>` con gli header `Dlq-Reason` (solo etichetta o tipo di eccezione, mai testo), `Dlq-Original-Subject`, `Dlq-Deliveries`, `Dlq-Consumer` e un `Nats-Msg-Id` deterministico `<DurableName>:<sequenza nello stream>`
(un crash tra DLQ e `Term`, o un ack della DLQ perso, non duplica la voce **entro la finestra di deduplicazione dello stream `DLQ`**, quella predefinita di 2 minuti; oltre, una seconda voce è possibile). Un ack della DLQ con `Duplicate = true` è un **successo** (la voce c'è già): il messaggio è terminato (`Term`). Il consumatore attende l'ack dello stream `DLQ` prima del `Term`; se la pubblicazione fallisce **non** termina il messaggio: `Nak` con attesa e nuovo tentativo.

**Avvio e arresto.** `ExecuteAsync` cede subito il controllo, attende `INatsStreamsReadiness.WhenReadyAsync` e crea/aggiorna (`CreateOrUpdate`) il consumatore durevole pull con backoff da 250 ms a 5 s: con NATS giù il servizio parte e il consumatore attende senza cicli serrati. Una configurazione rifiutata dal server (ad esempio una politica di partenza diversa da un consumatore esistente) è un log `Error` con codice ed errCode, poi nuovo tentativo. Se NATS viene riavviato durante il consumo il client rileva i battiti mancanti (`IdleHeartbeat` 3 s) e riprende da solo in pochi secondi.
**Arresto pulito.** Il gestore riceve la cancellazione, la transazione è annullata (nessuna riga di inbox, nessuna transazione appesa) e il messaggio **non è confermato**. Per renderlo subito riconsegnabile il consumatore invia un `Nak` senza attesa, ma solo **dopo** aver chiuso la sottoscrizione ai messaggi (altrimenti il server potrebbe riconsegnarlo alla richiesta di pull dell'istanza che si ferma, ormai chiusa) e lo fa seguire da un `PING`, perché la pubblicazione termina quando il comando è accodato e la chiusura della connessione scarta quanto non ancora scritto. Commit e `Ack` dopo il commit non dipendono dall'arresto (timeout propri).

**Arresto non pulito.** Se il `Nak` non parte (processo ucciso, connessione caduta, `Nak` perso) il messaggio in elaborazione resta non confermato e **la ripresa richiede fino ad `AckWait`**: con `MaxAckPending = MaxConcurrency` quel messaggio occupa gli slot e il consumatore riavviato (stesso durevole) non riceve altro finché `AckWait` non scade (60 s predefiniti). Nessun messaggio è perso né duplicato negli effetti (l'inbox lo garantisce): è solo una pausa. Con un `AckWait` più breve la pausa si accorcia, ma serve comunque a coprire le elaborazioni lunghe (il consumatore invia `AckProgress`).

**Configurazione del consumatore JetStream.** `AckPolicy` esplicita, `DeliverPolicy` All, `FilterSubject`, `AckWait`, `MaxAckPending = MaxConcurrency` e **`MaxDeliver = -1` lato server**.
Perché nessun `MaxDeliver` lato server: con il limite del server un processo morto durante l'ultimo tentativo farebbe scartare il messaggio da JetStream senza passare dalla DLQ (perdita silenziosa); il limite lo applica il consumatore confrontando `NumDelivered` con `ConsumerOptions.MaxDeliver`, e il messaggio finisce sempre in DLQ.
`MaxAckPending = MaxConcurrency` evita che i messaggi in attesa di uno slot siano già consegnati (e quindi con l'`AckWait` in corso): non ci sono riconsegne a vuoto con un backlog.

`EventConsumerStats` (singleton, `For(durableName)`) espone contatori thread-safe per diagnostica e test: `Received`, `Redelivered`, `Processed`, `Duplicates`, `Retried`, `DeadLettered`.

Garanzie e limiti:

- **Idempotenza garantita dal database**, non da controlli in memoria: la chiave primaria `(consumer, message_id)` e `ON CONFLICT DO NOTHING` valgono anche con più istanze del servizio sullo stesso consumatore durevole (la seconda `INSERT` attende il commit della prima e poi non scrive nulla). Un `Ack` perso dopo il commit produce solo una riconsegna confermata come duplicato.
- **Consegna at-least-once, effetto exactly-once sul database** del servizio. Gli effetti fuori dal database (chiamate di rete, file) non sono protetti: il gestore deve limitarsi a quel `DbContext`.
- **Un messaggio in ritentativo occupa il suo slot** (decisione di progetto: `MaxAckPending = MaxConcurrency` è **tenuto**). JetStream conta un messaggio con `Nak` ritardato ancora come non confermato: un messaggio che fallisce di continuo occupa uno slot per tutta la sua attesa di backoff. Con `MaxConcurrency = 1` e il backoff predefinito (1+2+5+10 s) ritarda i successivi di circa 18 s più la DLQ; con `MaxConcurrency = N` servono N messaggi problematici per fermare la coda. L'alternativa (`MaxAckPending` più alto con buffering lato client) rischia riconsegne spurie dei messaggi in attesa e falsi poison pill, quindi non è adottata. I test di integrazione dei servizi devono usare un `Backoff` breve.
- **Ordine.** Nessuna garanzia di ordine di elaborazione con `MaxConcurrency > 1`, né dopo un retry.
- **Ordine del controllo poison pill.** Il controllo `NumDelivered > MaxDeliver` precede l'inbox: un messaggio già elaborato, ma la cui `Ack` è andata persa più di `MaxDeliver` volte di fila, finisce in DLQ come `MaxDeliverExceeded` pur avendo prodotto i suoi effetti. Ripubblicarlo dalla DLQ è idempotente grazie all'inbox (il gestore non rigira).
- **Backoff predefinito totale: 18 s.** Un'interruzione del database più lunga manda gli eventi in DLQ (al quinto tentativo). Non esiste uno strumento di replay: il reinvio dalla DLQ è manuale.
- **Dimensione del messaggio in DLQ.** Gli header `Dlq-*` si sommano al payload: un messaggio a meno di circa 150 byte da `max_payload` (8 MB) non è inseribile nella DLQ. La pubblicazione fallisce, il messaggio **non** è terminato e resta in `Nak` con attesa, riprovando a ogni consegna (log `Error` con il tipo dell'errore); con `MaxConcurrency = 1` occupa lo slot. Un messaggio così grande va corretto a monte (limite di punti, DEC-012).
- **Metadati assenti.** Se i metadati del messaggio non sono leggibili il consumatore assume una sola consegna e sequenza 0: il `Nats-Msg-Id` DLQ diventa costante (`<DurableName>:0`) e un secondo messaggio con lo stesso problema, entro la finestra di deduplicazione, non produrrebbe una nuova voce in DLQ. Non succede con messaggi JetStream normali.
- **`FilterSubject` non accetta `*`** (scelta dichiarata): un solo subject esatto, o con `>` finale, per consumatore.
- **Commit con esito incerto** (connessione persa durante il commit): il messaggio è ritentato e trova la riga di inbox, quindi è confermato come duplicato; se l'incertezza cade sull'ultimo tentativo il messaggio va in DLQ anche se l'evento era stato elaborato.
- **L'inbox cresce senza limite**: nessuna pulizia automatica (una riga per evento e consumatore). Una pulizia periodica per `processed_at` va decisa a parte e deve restare sotto la finestra in cui un evento può ancora essere riconsegnato.
- **I messaggi in DLQ vanno ispezionati a mano**: nessun reinvio automatico (stream `DLQ`, subject `dlq.<DurableName>`; il payload originale è intatto, `Dlq-Original-Subject` indica il subject di origine per un eventuale reinvio manuale).
- Il gestore **non deve** aprire transazioni proprie né fare commit; un `SaveChanges` nel gestore partecipa alla transazione del consumatore. Con una strategia di retry di EF (`EnableRetryOnFailure`) il gestore può essere rieseguito dalla strategia nello stesso tentativo: non deve avere effetti fuori dal contesto.
- Nei log del consumatore compaiono solo nome del consumatore, sequenza nello stream, conteggi, etichette e tipi di eccezione: mai payload, subject o testi di eccezione (per questo anche il testo di `EventValidationException` non è registrato).
