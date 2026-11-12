# 07 · Integrazioni

Tutte le integrazioni vivono in `ingestion` dietro un'interfaccia comune: il resto del sistema vede solo il **modello comune di attività**.

## Modello comune (idea)

```text
ActivityImported
├── source: strava | zepp | file
├── externalId            (per la deduplicazione: fonte + id)
├── sport: run | ride | swim | other
├── startTime (UTC), elapsedTime, movingTime
├── distance, elevationGain
├── summary: velocità media, FC media/max, potenza media/normalizzata, calorie…
├── laps[]
└── streams: time, position, altitude, heartRate, power, cadence, speed, …
```

Dettagli per sport (es. vasche e SWOLF per il nuoto) in campi opzionali per sport.

## Strava

- **Autenticazione:** OAuth 2.0; ambiti minimi per leggere le attività.
- **Ingestione in tempo reale:** sottoscrizione ai **webhook** di Strava (creazione, modifica, cancellazione di attività e revoca dell'autorizzazione).
- **Recupero storico:** all'avvio del collegamento si importano le attività passate a ritmo controllato.
- **Flusso del webhook:** risposta immediata, accodamento, scarico dell'attività e degli stream con il token dell'utente, traduzione nel modello comune, evento `ActivityImported`.
- **Limiti:** l'API ha limiti di richieste per finestra di 15 minuti e giornalieri **[da verificare sulla documentazione attuale]**. La coda di `ingestion` li rispetta e riprova dopo un errore 429.
- **Termini d'uso:** le regole di Strava sull'uso e la conservazione dei dati (e sulla loro visualizzazione ad altri utenti) vanno lette **prima** di aprire il servizio a terzi. Per l'uso personale il rischio è basso; per un prodotto pubblico è da chiarire.
- **Tracciamento:** in sviluppo serve un indirizzo pubblico (tunnel) per ricevere i webhook.

## Bryton

Nessuna integrazione diretta: l'app Bryton si collega a Strava e le attività arrivano dal flusso Strava. Se in futuro serve un accesso diretto, sarà un nuovo connettore.

## Zepp / Amazfit

- Nel PoC usa un'API **non ufficiale** (Mifit), con token manuale.
- Rischi: può cambiare o essere bloccata senza preavviso; richiede un token ottenuto fuori dal flusso ufficiale.
- Decisione: connettore **isolato e opzionale**, dopo l'MVP. Se l'API cambia, si disattiva senza toccare il resto. In alternativa, importare i file esportati (FIT/GPX).

## File FIT e GPX

- Caricamento manuale dal frontend.
- **FIT:** formato binario dei dispositivi, con dati completi (FC, potenza, cadenza, vasche). Libreria .NET per il parsing da scegliere (es. SDK FIT di Garmin per .NET o una libreria di terze parti) **[da valutare]**.
- **GPX:** XML con tracce e punti; meno ricco, parsing semplice.
- Precauzioni: limite di dimensione, tempo massimo di parsing, nessuna fiducia nel contenuto (vedi [06-security.md](06-security.md)).
- Deduplicazione rispetto alle attività già arrivate da Strava (stesso orario di inizio, durata e distanza compatibili).

## Altre fonti (idee)

Garmin Connect, Coros, Wahoo, Polar, Suunto: ognuna ha API con requisiti di accesso propri. Ogni nuova fonte è un connettore in `ingestion`, senza impatto sugli altri servizi.

## Aggiungere una fonte

1. Nuovo connettore in `ingestion` che implementa l'interfaccia comune.
2. Traduzione nel modello comune e test con file/risposte di esempio.
3. Nessuna modifica a `activities`, `analytics`, `training-plan`: è lo scopo del modello comune.
