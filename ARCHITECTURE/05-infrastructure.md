# 05 · Infrastruttura

## Contenitori: Docker o Podman

Obiettivo: lo stesso progetto parte con **Docker** o con **Podman**, senza modifiche ([ADR 0006](adr/0006-contenitori-docker-podman.md)).

Regole per restare neutri:

- Immagini in formato **OCI**, costruite da `Dockerfile` standard (Podman li legge).
- Orchestrazione locale con un unico **`compose.yaml`** in formato Compose standard, avviabile con `docker compose` o `podman compose`.
- Nessun uso di funzioni esclusive di un solo runtime (socket di Docker montato nei contenitori, `docker.sock`, estensioni specifiche).
- Nomi di immagine completi (con registro), perché Podman non assume Docker Hub per i nomi brevi.
- Volumi con attenzione ai permessi: Podman *rootless* mappa gli utenti in modo diverso; si usa l'opzione di etichetta `:Z` dove serve su Linux con SELinux.
- Nei Dockerfile, utente non privilegiato.

## Sviluppo locale

Due livelli, a scelta:

1. **Solo infrastruttura nei contenitori:** database e bus partono da `deploy/compose.yaml`; i servizi .NET e il frontend si eseguono dal proprio editor con ricarica a caldo. È il modo più comodo per sviluppare.
2. **Tutto nei contenitori:** compose avvia anche i servizi, per provare l'insieme come in produzione.

**.NET Aspire** **[Ipotesi]** come orchestratore di sviluppo: un progetto *AppHost* descrive servizi, database e bus, li avvia insieme, fornisce una dashboard con log, tracce e metriche e collega automaticamente le stringhe di connessione. Può usare Docker o Podman come runtime dei contenitori. Da verificare nella pratica: se aggiunge più attrito che valore, si resta sul solo `compose.yaml`.

## Contenitori previsti

| Contenitore | Immagine | Note |
|---|---|---|
| `postgres` | PostgreSQL con TimescaleDB | Volume persistente; script di creazione schemi e utenti |
| `nats` | NATS con JetStream abilitato | Volume per la persistenza dei flussi |
| `gateway`, `identity`, `ingestion`, `activities`, `analytics`, `training-plan` | Immagine .NET per servizio | Costruite con build in più fasi (immagine finale minima) |
| `web` | File statici serviti da un web server leggero | Nginx o Caddy; proxy verso il gateway |

## Configurazione e segreti

- Configurazione tramite **variabili d'ambiente** e file `.env` locali **non versionati**; nel repository solo `.env.example`.
- Segreti (secret di Strava, chiavi di cifratura) mai nel codice né nelle immagini. In produzione tramite i segreti del runtime o un gestore dedicato.
- Un tunnel (come `ngrok` nel PoC) o un dominio pubblico serve in sviluppo per ricevere i webhook di Strava.

## Osservabilità

- **OpenTelemetry** in ogni servizio: tracce, metriche e log strutturati con `correlationId`.
- Le tracce seguono una richiesta dal gateway agli eventi che ne derivano (il contesto di traccia viaggia nella busta degli eventi).
- In sviluppo: dashboard di Aspire, oppure un collettore OTel con Grafana/Tempo/Loki/Prometheus in un profilo compose opzionale **[Ipotesi]**.
- Endpoint di **salute** (`/health/live`, `/health/ready`) su ogni servizio, usati dai controlli di compose.

## Integrazione continua

**[Ipotesi]** Pipeline per servizio, avviata solo quando cambiano i suoi file: compilazione, test, costruzione dell'immagine. Test di integrazione con contenitori reali per database e bus (Testcontainers). Piattaforma da scegliere (es. GitHub Actions).

## Produzione

Da decidere. Opzioni realistiche per un singolo sviluppatore, dalla più semplice: un server con compose; un server con Podman e `systemd`/Quadlet; un cluster Kubernetes gestito (più impegnativo, valore didattico alto). Non è una decisione da prendere ora: l'architettura non dipende da questa scelta.
