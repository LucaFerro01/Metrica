# ADR 0003 · NATS JetStream come bus di eventi

- **Stato:** Proposta (da confermare con un prototipo)
- **Data:** 2026-11-12

## Contesto

I servizi devono scambiarsi eventi in modo affidabile (almeno una volta, con persistenza e riconsegna). Il PoC usava Redis con BullMQ come coda di lavoro, che non è un bus di eventi tra servizi.

## Decisione

Si propone **NATS con JetStream** come bus di eventi.

## Motivazioni

- Molto **leggero**: un solo binario, avvio immediato, poca memoria. Adatto a sviluppo locale e a un singolo server.
- JetStream offre persistenza, consumatori durevoli, riconsegna e conferme.
- Client .NET ufficiale e mantenuto.
- Semplice da far girare con Docker o Podman.
- Coerente con la richiesta di tecnologie leggere e reattive.

## Conseguenze

- Meno strumenti e meno esperienza diffusa rispetto a RabbitMQ o Kafka; più documentazione da leggere direttamente.
- Outbox, idempotenza e coda dei messaggi falliti vanno implementate dai servizi (vedi [02-communication.md](../02-communication.md)).
- Senza astrazioni da subito: si incapsula l'accesso al bus in un piccolo componente per poterlo sostituire.

## Alternative considerate

- **RabbitMQ:** molto diffuso, ottimo per code di lavoro e routing, ricco di strumenti (MassTransit in .NET). Più pesante e con un modello di persistenza diverso. È l'alternativa principale: se il prototipo con NATS risulta scomodo, si passa a questa.
- **Kafka:** potente ma sproporzionato per un progetto piccolo.
- **Redis Streams / BullMQ:** valido per code, meno adatto come bus di eventi tra servizi e ricco di compromessi sulla durabilità.
- **Un'astrazione come MassTransit sopra il trasporto:** utile con RabbitMQ; con NATS il supporto è meno maturo. Da riconsiderare.

## Come confermare

Prototipo con due servizi: pubblicazione di `ActivityImported` con outbox, consumo idempotente, riconsegna dopo un errore forzato. Se funziona senza attrito, lo stato passa ad *Accettata*.
