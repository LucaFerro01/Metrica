# ADR 0003 · NATS JetStream come bus di eventi

- **Stato:** Accettata (2026-11-20, confermata dallo scheletro del sistema: vedi *Esito dello scheletro del sistema*)
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

Eseguito con lo scheletro del sistema (`ingestion` pubblica, `activities` consuma): vedi la sezione seguente.

## Esito dello scheletro del sistema (2026-11-20)

Sezione aggiornata con l'esito dello scheletro, come previsto dal piano T20 (verifica finale e aggiornamento dell'architettura). Il cambio di stato a *Accettata* è quello previsto dal piano e dalla sezione «Come confermare»; nessun altro stato è cambiato.

Il prototipo ha confermato NATS JetStream: lo stato passa ad *Accettata*. Non è stato necessario ripiegare su RabbitMQ e le modifiche sono rimaste confinate nel progetto condiviso `Metrica.Messaging` (solo infrastruttura, dipende solo da `contracts`: DEC-001). Con `NATS.Net` 3.3.2 sono implementati e provati dai test (`src/shared/Messaging/tests`, `tests/Metrica.IntegrationTests`):

- **Outbox e relay** (`OutboxRelay`): evento scritto nella stessa transazione dei dati, pubblicazione in background con `FOR UPDATE SKIP LOCKED`, backoff con NATS giù, ack `Duplicate` trattato come successo. Con NATS fermo il caricamento riesce e l'evento arriva al riavvio, un solo messaggio per file (AC-005, `NatsOutageTests`, con NATS fermato e riavviato sulla stessa porta).
- **Inbox e consumatore** (`EventConsumer`): `INSERT ... ON CONFLICT DO NOTHING` sulla chiave `(consumer, message_id)`, gestore e commit nella stessa transazione, `Ack` solo dopo il commit, `AckProgress` per le elaborazioni lunghe. Stessa busta consegnata due volte = una sola attività (AC-006).
- **Retry con backoff e DLQ**: `NakWithDelay` con attesa crescente (predefinito 1, 2, 5, 10 s, `MaxDeliver` 5), poi stream `DLQ` con le intestazioni `Dlq-*` e `Nats-Msg-Id` deterministico; un evento non valido va in DLQ al primo tentativo (`EventValidationException`, AC-012).
- **Riconsegna**: riavvio del consumatore a metà elaborazione senza perdite né effetti duplicati (`RestartInTheMiddle`, con `AckWait` di 5 s e di 60 s) e ripresa dopo il riavvio di NATS senza riavviare il servizio.

Problemi incontrati e soluzioni:

- **Subject sovrapposti tra `EVENTS` e `DLQ`** (emerso nella revisione del piano, DEC-005): JetStream rifiuta stream con subject sovrapposti, quindi `EVENTS` non può usare un pattern generico (`*.*.*` si sovrapporrebbe a `dlq.>`). Gli stream sono dichiarati con prefissi espliciti (`identity.>`, `ingestion.>`, `activities.>`, `analytics.>`, `training-plan.>`; `dlq.>` per `DLQ`); i test `Subjects_DoNotOverlapBetweenStreams` e `DlqSubject_DoesNotEnterEventsStream` lo fissano. Un nuovo servizio richiede di aggiungere il suo prefisso.
- **Dimensione dei messaggi** (DEC-012): il `max_payload` predefinito del server (1 MB) non regge un'attività lunga; `deploy/nats/nats-server.conf` lo porta a 8 MiB (`8MB` nella configurazione NATS vale 8.388.608 byte) e `EVENTS` non imposta `max_msg_size`. Per restare sotto il limite `ingestion` arrotonda i valori dello stream e rifiuta più di 100.000 punti (il caso peggiore con 100.000 punti e tutte le colonne sta sotto 8 MiB, test di `contracts`: circa 6,4 MB secondo DEC-012; 100.000 punti importati davvero in AC-014 con l'evento misurato dal server sotto 8.000.000 byte). Un messaggio oltre `max_payload` è rifiutato dal client (`NatsPayloadTooLargeException`) e il relay non si blocca su di esso: un primo difetto (un messaggio da 9 MB fermava tutta la coda) è stato trovato in revisione, corretto e coperto da un test. Il rifiuto lato server oltre gli 8 MiB non è provato (i test passano dal controllo del client).
- **Deduplicazione lato server**: il `Nats-Msg-Id` è l'id della busta e `EVENTS` ha una finestra di 1 ora; oltre la finestra (e per la DLQ, che usa la finestra predefinita di 2 minuti) serve l'idempotenza del consumatore, cioè l'inbox. Un ack `Duplicate` della DLQ trattato come errore generava un ciclo di `Nak` (difetto trovato in revisione, corretto).
- **`MaxAckPending = MaxConcurrency`** (decisione di T07): un messaggio in ritentativo occupa il suo slot, quindi con concorrenza 1 un messaggio che fallisce di continuo ritarda i successivi di circa 18 s più la DLQ. «Non bloccano gli altri» (REQ-017) significa «con un ritardo limitato»; l'alternativa con buffering lato client rischia riconsegne spurie e falsi messaggi velenosi. Il test di AC-012 prova il ritardo limitato, non l'assenza di ritardo.
- **`MaxDeliver = -1` lato server**: il limite lo applica il consumatore confrontando `NumDelivered` con `ConsumerOptions.MaxDeliver`, così il messaggio finisce sempre in DLQ; con il limite del server un processo morto all'ultimo tentativo farebbe scartare il messaggio senza DLQ.
- **Arresto del consumatore**: il `Nak` di arresto va inviato solo dopo la chiusura della sottoscrizione e seguito da un `PING`, altrimenti il messaggio si riconsegna a una richiesta di pull ormai chiusa o il `Nak` si perde con la connessione (cause di un test intermittente, diagnosticate con prove di stress). Con un arresto non pulito (processo ucciso) la ripresa richiede fino ad `AckWait` (60 s predefiniti).
- **Durevole `activities-activity-imported`**: il nome non contiene punti perché è anche il subject DLQ (`dlq.activities-activity-imported`) e la colonna `consumer` dell'inbox; `FilterSubject` accetta un subject esatto o con `>` finale, non `*`.
- **Avvio con NATS fermo** (REQ-013): gli stream e il consumatore si creano in background con backoff (250 ms, massimo 5 s) e il servizio parte comunque; `/health/ready` non controlla NATS. Una configurazione di stream rifiutata dal server (ad esempio lo storage, che non si cambia su uno stream esistente) è un log `Error` con il codice del server, non un ciclo silenzioso.
- **Fixture di test con porta NATS fissa** (DEC-013): con una porta casuale il riavvio del contenitore cambierebbe l'indirizzo e AC-005 non potrebbe riconnettersi; `NatsFixture` usa porte host fisse scelte una volta. Le porte sono scelte a caso e controllate (`FreePortAllocator`) dopo un test intermittente per porta occupata.
- **Copia degli array dei subject** prima di passarli a `NATS.Net` (gli array di `EventNames` sono mutabili) e `Nats:Url` letto alla prima risoluzione, così gli override dei test sono rispettati.

Limiti noti e non provati:

- Il percorso `MaxDeliverExceeded` (messaggio velenoso con processo morto) è provato nei test di `Messaging` con consegne simulate e non negli scenari di integrazione con un processo ucciso; la rete di sicurezza `EventTooBig` di `ingestion` non è esercitata.
- AC-006 è un test di idempotenza con ripubblicazione della stessa busta, non di una riconsegna JetStream per ack perso; la riconsegna vera è coperta dai test di `Messaging`.
- Non provati: più istanze dello stesso durevole, un crash reale del processo in tutte le fasi, `EnableRetryOnFailure` di EF, TLS, autenticazione su NATS (in sviluppo il server non ha credenziali e il compose lo pubblica solo su `127.0.0.1`).
- Nessuno strumento di reinvio dalla DLQ (il reinvio è manuale), nessuna pulizia dell'inbox, nessun parcheggio automatico dei messaggi che falliscono sempre nell'outbox (restano con `Attempts` crescente); se 50 messaggi problematici sono in testa all'outbox, i successivi non vengono mai pubblicati. Dettagli in [`src/shared/Messaging/README.md`](../../src/shared/Messaging/README.md).
- L'immagine `docker.io/library/nats:2` non è fissata a una versione precisa (come `timescaledb:latest-pg16`, DEC-013).
