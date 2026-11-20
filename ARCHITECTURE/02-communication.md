# 02 · Comunicazione

## Due modi di parlarsi

| Tipo | Quando | Come |
|---|---|---|
| **Sincrona** | Il client (o un servizio) ha bisogno di una risposta ora: leggere dati, eseguire un comando dell'utente | HTTP/JSON con OpenAPI, attraverso il gateway per i client |
| **Asincrona** | Qualcosa è successo e altri servizi possono reagire; lavoro lungo o ripetibile | Eventi su NATS JetStream |

Regola pratica: **un comando dell'utente = una chiamata sincrona a un solo servizio**. Se serve coordinare più servizi, quel servizio pubblica un evento e gli altri reagiscono.

Tra servizi si evitano catene di chiamate sincrone (A chiama B che chiama C): rendono la disponibilità il prodotto di quella di tutti.

## Eventi

Gli eventi sono **fatti già accaduti**, nominati al passato: `ActivityImported`, `ActivityStored`, `ThresholdsChanged`.

Ogni evento ha una busta comune:

```json
{
  "id": "uuid",
  "type": "ActivityStored",
  "version": 1,
  "occurredAt": "2026-11-12T08:30:00Z",
  "userId": "uuid",
  "correlationId": "uuid",
  "data": { }
}
```

- `id` serve alla deduplicazione (è anche il `Nats-Msg-Id`); `correlationId` alla tracciatura distribuita.
- `version` permette di far evolvere gli eventi: si **aggiungono** campi opzionali; un cambio incompatibile richiede una nuova versione, con le due in parallelo per un periodo.
- Le definizioni vivono in [`src/shared/contracts`](../src/shared/contracts/README.md) (record C#, JSON con `ContractJson.Options`: camelCase, enum come stringhe minuscole, `null` espliciti). Il codice condiviso tra servizi è limitato a `contracts`, `ServiceDefaults` e `Messaging` (vedi [01-services.md](01-services.md)).
- I payload contengono il necessario per reagire. **Realizzato per `ActivityImported`** (DEC-005, DEC-012): porta anche lo **stream completo in formato a colonne** (un array per campo, `null` per i campi assenti, tempo come scarti in secondi dall'inizio), con valori **arrotondati dal produttore** (tempo a 0,001 s, latitudine e longitudine a 7 decimali, quota a 0,1 m, velocità a 0,001 m/s, frequenza cardiaca, potenza e cadenza interi), **al massimo 100.000 punti** e in ogni caso sotto gli 8 MiB (il limite `max_payload` del server NATS: `8MB` nella sua configurazione vale 8 MiB, cioè 8.388.608 byte). Un file oltre i 100.000 punti è rifiutato da `ingestion` prima di produrre l'evento. Se servissero file più grandi si passerebbe a un riferimento (*claim check*, per esempio con l'Object Store di JetStream) con una nuova `version` dell'evento **[Ipotesi]**.
- Il consumatore valida l'evento prima di toccare il database (versione e tipo della busta, campi obbligatori, colonne di uguale lunghezza, numeri finiti, `Sport` definito, al massimo 100.000 punti): un evento non valido va direttamente in DLQ, senza ritentativi. Un `sport` o numeri obbligatori mancanti nel JSON non sono distinguibili da un valore predefinito (`run`, 0): serve rendere obbligatori i campi nei contratti (limite noto).

### Nomi dei soggetti (subject) NATS

Convenzione: `<servizio>.<entità>.<evento>`, ad esempio `ingestion.activity.imported` (l'unico evento oggi pubblicato) o `activities.activity.stored` (previsto). I consumatori si iscrivono a un subject esatto o con un `>` finale; il consumatore di `Messaging` non accetta `*` nel filtro.

### Stream JetStream

Due stream, creati in modo idempotente all'avvio di ogni servizio che usa `Messaging` (oggi `ingestion` e `activities`; il gateway no) (in background, con tentativi: il servizio parte anche con NATS fermo):

| Stream | Subject | Note |
|---|---|---|
| `EVENTS` | `identity.>`, `ingestion.>`, `activities.>`, `analytics.>`, `training-plan.>` | storage su file, finestra di deduplicazione di 1 ora, nessun `max_msg_size` (vale il `max_payload` del server) |
| `DLQ` | `dlq.>` (un subject `dlq.<consumatore>` per ogni consumatore) | storage su file |

I prefissi di `EVENTS` sono **espliciti** e non un pattern generico come `*.*.*`: JetStream rifiuta stream con subject sovrapposti e `*.*.*` si sovrapporrebbe a `dlq.>` (DEC-005). Un nuovo servizio aggiunge il proprio prefisso. Il server NATS di sviluppo è configurato con `max_payload` di 8 MiB (`deploy/nats/nats-server.conf`, il predefinito è 1 MB). Ogni consumatore crea il proprio consumatore durevole (per `activities`: `activities-activity-imported`).

## Affidabilità

JetStream garantisce consegna **almeno una volta**: un messaggio può arrivare più di una volta. Per questo i punti 1, 2, 3 e 5 sono **implementati** in `Metrica.Messaging` e **provati** dai test (vedi [ADR 0003](adr/0003-messaggistica-nats.md)); i punti 4 (ordinamento) e 6 (correlazione) sono **previsti** o solo parzialmente realizzati, come indicato:

1. **Consumatori idempotenti (inbox).** Il servizio che consuma ha una tabella `inbox` con chiave primaria `(consumer, message_id)`; per ogni messaggio, in **una sola transazione**, inserisce la riga con `ON CONFLICT DO NOTHING`, esegue il gestore e fa il commit, poi conferma (`Ack`) **solo dopo il commit**. Se la riga esisteva già il messaggio è un duplicato: il gestore non gira e il messaggio è confermato. L'idempotenza è garantita dal database (chiave primaria e `ON CONFLICT`); è provata con transazioni concorrenti nello **stesso processo** (`ActivityImportedHandlerTests` con la stessa chiave in due transazioni concorrenti; `ConsumerTests` con concorrenza 3, dove ogni messaggio è elaborato una sola volta), **non** con più processi sullo stesso durevole né con un crash reale in ogni fase. Gli effetti fuori dal database non sono protetti.
2. **Outbox transazionale e relay.** Un servizio che modifica il proprio database e pubblica un evento scrive l'evento nella tabella `outbox` **nella stessa transazione**; un `BackgroundService` (`OutboxRelay`) legge le righe non pubblicate con `FOR UPDATE SKIP LOCKED` (due istanze non fanno doppio lavoro: provato con due relay concorrenti nello stesso processo, `OutboxRelayTests`, non con più processi), le pubblica con `Nats-Msg-Id` uguale all'id della busta e le marca pubblicate. Un ack `Duplicate` del server (crash tra pubblicazione e marcatura) vale come successo. Con NATS fermo il servizio parte, il caricamento riesce e il relay pubblica al ritorno del bus con backoff crescente (REQ-013). La deduplicazione di JetStream copre solo la finestra di 1 ora: oltre serve l'inbox.
3. **Ripetizione e coda dei messaggi falliti (DLQ).** Un errore del gestore, del database o del commit annulla la transazione e riprogramma il messaggio con attesa crescente (`NakWithDelay`; predefinito 1, 2, 5, 10 s, `MaxDeliver` 5, quindi circa 18 s prima della DLQ). Un evento non valido o malformato va in DLQ al primo tentativo. In DLQ (stream `DLQ`, subject `dlq.<consumatore>`) il messaggio resta **con il payload originale** e le intestazioni `Dlq-Reason` (etichetta o tipo di eccezione, mai testo), `Dlq-Original-Subject`, `Dlq-Deliveries` e `Dlq-Consumer`; il suo `Nats-Msg-Id` è deterministico, quindi un crash tra DLQ e conferma non duplica la voce (entro la finestra di 2 minuti della DLQ). Il limite `MaxDeliver` lo applica il consumatore (lato server è `-1`) così il messaggio finisce sempre in DLQ. Non esiste ancora uno strumento di reinvio: la DLQ si ispeziona e si reinvia a mano.
4. **Ordinamento** (*previsto come principio di progetto; non c'è un test sull'ordine*). Non è garantito tra eventi diversi, né dopo un retry; con `MaxConcurrency > 1` neppure l'ordine di elaborazione. Il consumatore di `activities` ha concorrenza 1 e `MaxAckPending = MaxConcurrency`: un messaggio che fallisce di continuo occupa il suo slot e ritarda i successivi per la durata del backoff (circa 18 s più la DLQ), non li blocca per sempre. L'ordine di pubblicazione del relay segue `created_at` solo all'interno di un'istanza. Si progetta perché l'ordine non conti (es. usare `occurredAt` e versioni sull'entità) o si usa la chiave di partizione per utente dove serve **[Ipotesi]**.
5. **Riconsegna dopo un arresto.** Un arresto pulito del consumatore invia un `Nak` e il messaggio è riconsegnato subito; con un arresto non pulito (processo ucciso) la ripresa richiede fino ad `AckWait` (60 s predefiniti).
6. **Correlazione** (*in parte prevista*). `ingestion` scrive nella busta il `correlationId` della richiesta (l'header `X-Correlation-Id` se è un UUID valido, altrimenti uno generato). **Non** viene ancora ripreso dal consumatore nei log di `activities` né esiste un contesto di traccia nella busta: previsti.

Limiti noti non risolti (README di [`Messaging`](../src/shared/Messaging/README.md)): l'inbox cresce senza pulizia, l'outbox non parcheggia i messaggi che falliscono sempre (se 50 messaggi problematici sono in testa, i successivi non vengono mai pubblicati), un'interruzione del database più lunga del backoff manda gli eventi in DLQ.

## Consistenza

I dati tra servizi sono **eventualmente consistenti**: dopo un import, l'attività appare in `activities` subito, ma le metriche in `analytics` arrivano pochi istanti dopo. La UI deve poterlo mostrare (stato "in calcolo") senza trattarlo come un errore. Nello scheletro vale già per l'import: il web mostra una riga «in elaborazione» finché l'attività non compare in `activities`, riconosciuta in modo esatto con il filtro `externalId` e con un polling a 2 secondi (DEC-008, DEC-017); `analytics` non esiste ancora.

## Copie locali (read model)

Quando un servizio ha spesso bisogno di dati di un altro (es. `analytics` ha bisogno di riepiloghi di attività), tiene una **copia ridotta** mantenuta aggiornata via eventi, invece di chiamare l'altro servizio a ogni richiesta. Si accetta la duplicazione in cambio di autonomia e velocità.

## Cancellazione dei dati

`UserDeleted` (pubblicato da `identity`) è consumato da tutti i servizi, che cancellano i dati dell'utente e confermano. Serve a rispettare il diritto alla cancellazione (vedi [06-security.md](06-security.md)).

## Sicurezza tra servizi

- I servizi non sono esposti all'esterno: solo il gateway lo è. **Nello sviluppo locale** nulla è raggiungibile dalla rete: i servizi ascoltano su `127.0.0.1`, il gateway e il web su `localhost`, PostgreSQL e NATS sono pubblicati solo su `127.0.0.1` (DEC-009).
- La rete interna usa nomi di servizio; la comunicazione con NATS usa credenziali per servizio **[Ipotesi]**: oggi il server NATS di sviluppo non ha autenticazione.
- L'identità dell'utente viaggia in un token validato dal gateway e riverificato dai servizi **[Ipotesi]**. Oggi i servizi leggono l'utente solo dal proprio principal, fornito in `Development` dall'identità di sviluppo fissa, e il gateway toglie le intestazioni di identità del client (vedi [06-security.md](06-security.md)).

## Alternative valutate

Vedi [ADR 0003](adr/0003-messaggistica-nats.md) (NATS contro RabbitMQ) e [ADR 0002](adr/0002-microservizi.md) (perché non un monolite modulare).
