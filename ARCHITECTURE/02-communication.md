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

- `id` serve alla deduplicazione; `correlationId` alla tracciatura distribuita.
- `version` permette di far evolvere gli eventi: si **aggiungono** campi opzionali; un cambio incompatibile richiede una nuova versione, con le due in parallelo per un periodo.
- Le definizioni vivono in [`src/shared/contracts`](../src/shared/contracts/README.md) e sono l'unico codice condiviso tra servizi.
- I payload contengono il necessario per reagire; per i dati voluminosi (stream completi) si pubblica un riferimento o un riepilogo e si legge il resto con un'API **[Ipotesi]**.

### Nomi dei soggetti (subject) NATS

Convenzione: `<servizio>.<entità>.<evento>`, ad esempio `activities.activity.stored`. I consumatori si iscrivono con filtri (`activities.activity.*`).

## Affidabilità

JetStream garantisce consegna **almeno una volta**: un messaggio può arrivare più di una volta. Per questo:

1. **Consumatori idempotenti.** Ogni consumatore registra gli `id` già elaborati (o usa vincoli di unicità sul dato) e ignora i doppioni.
2. **Outbox transazionale.** Un servizio che modifica il proprio database e pubblica un evento scrive l'evento in una tabella `outbox` **nella stessa transazione**; un processo in background la svuota verso NATS. Così non si perde né si inventa un evento se il servizio si ferma a metà.
3. **Ripetizione e coda dei messaggi falliti.** Tentativi con attesa crescente; dopo N tentativi il messaggio va in una *dead-letter* da ispezionare, non si perde.
4. **Ordinamento.** Non è garantito tra eventi diversi. Si progetta perché l'ordine non conti (es. usare `occurredAt` e versioni sull'entità) o si usa la chiave di partizione per utente dove serve.

## Consistenza

I dati tra servizi sono **eventualmente consistenti**: dopo un import, l'attività appare in `activities` subito, ma le metriche in `analytics` arrivano pochi istanti dopo. La UI deve poterlo mostrare (stato "in calcolo") senza trattarlo come un errore.

## Copie locali (read model)

Quando un servizio ha spesso bisogno di dati di un altro (es. `analytics` ha bisogno di riepiloghi di attività), tiene una **copia ridotta** mantenuta aggiornata via eventi, invece di chiamare l'altro servizio a ogni richiesta. Si accetta la duplicazione in cambio di autonomia e velocità.

## Cancellazione dei dati

`UserDeleted` (pubblicato da `identity`) è consumato da tutti i servizi, che cancellano i dati dell'utente e confermano. Serve a rispettare il diritto alla cancellazione (vedi [06-security.md](06-security.md)).

## Sicurezza tra servizi

- I servizi non sono esposti all'esterno: solo il gateway lo è.
- La rete interna usa nomi di servizio; la comunicazione con NATS usa credenziali per servizio.
- L'identità dell'utente viaggia in un token validato dal gateway e riverificato dai servizi **[Ipotesi]**.

## Alternative valutate

Vedi [ADR 0003](adr/0003-messaggistica-nats.md) (NATS contro RabbitMQ) e [ADR 0002](adr/0002-microservizi.md) (perché non un monolite modulare).
