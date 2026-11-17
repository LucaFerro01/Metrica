# Architettura

Documentazione delle scelte architetturali. Il [README principale](../README.md) descrive il prodotto; qui si spiega **come è costruito e perché**.

## Indice

| File | Contenuto |
|---|---|
| [00-overview.md](00-overview.md) | Contesto, obiettivi di qualità, vincoli, vista d'insieme |
| [01-services.md](01-services.md) | Servizi, responsabilità, dati posseduti, API |
| [02-communication.md](02-communication.md) | Comunicazione sincrona e asincrona, eventi, affidabilità |
| [03-data.md](03-data.md) | Database, schemi, serie temporali, migrazioni |
| [04-frontend.md](04-frontend.md) | Frontend React, PWA, micro-frontend |
| [05-infrastructure.md](05-infrastructure.md) | Contenitori (Docker/Podman), Aspire, CI, osservabilità |
| [06-security.md](06-security.md) | Autenticazione, token, dati personali |
| [07-integrations.md](07-integrations.md) | Strava, Zepp, FIT/GPX, Bryton |
| [08-analytics.md](08-analytics.md) | Modello delle metriche e delle stime |
| [adr/](adr/) | Registro delle decisioni, una per file |

## Come si usa questa cartella

- Le **decisioni** vivono negli ADR (*Architecture Decision Record*) in `adr/`. Ogni ADR ha stato `Proposta`, `Accettata`, `Superata` o `Scartata`, e non si riscrive: se cambia idea, se ne crea uno nuovo che sostituisce il precedente.
- I file numerati descrivono lo **stato attuale** e rimandano agli ADR per le motivazioni.
- Convenzione: ciò che è una scelta già presa è scritto al presente; ciò che è un'ipotesi è marcato **[Ipotesi]**.

## Decisioni prese

| ADR | Decisione | Stato |
|---|---|---|
| [0001](adr/0001-backend-csharp-dotnet.md) | Backend in C# / .NET | Accettata |
| [0002](adr/0002-microservizi.md) | Architettura a microservizi | Accettata |
| [0003](adr/0003-messaggistica-nats.md) | NATS JetStream come bus di eventi | Proposta |
| [0004](adr/0004-database-postgres-timescale.md) | PostgreSQL con TimescaleDB, uno schema per servizio | Proposta |
| [0005](adr/0005-frontend-react.md) | Frontend React + TypeScript + Vite | Accettata |
| [0006](adr/0006-contenitori-docker-podman.md) | Contenitori neutri tra Docker e Podman | Accettata |
| [0007](adr/0007-micro-frontend.md) | Micro-frontend: non adottati all'inizio | Proposta |
| [0008](adr/0008-frontend-responsivo-con-guscio-nativo.md) | Front-end responsivo con due layout, predisposto per un guscio nativo | Accettata |

## Decisioni aperte

- **Micro-frontend:** si parte con un'unica app modulare; la scelta va rivalutata quando la UI cresce (ADR 0007).
- **Accesso ai dati:** EF Core ovunque, oppure Dapper per i percorsi con serie temporali (ADR 0004).
- **Bus di eventi:** NATS JetStream contro RabbitMQ; da confermare con un prototipo (ADR 0003).
- **Autenticazione:** identity interno (ASP.NET Core Identity + OpenIddict) o provider esterno; vedi [06-security.md](06-security.md).
- **Libreria di grafici e mappe:** Recharts, visx o ECharts; MapLibre per le mappe. Da decidere dopo un prototipo.
- **Modello multi-utente:** solo atleti singoli all'inizio; chi pianifica può essere un allenatore oppure l'atleta stesso (vedi [04-frontend.md](04-frontend.md)): il ruolo allenatore cambia i confini di `identity` e `training-plan`.
- **Guscio nativo e store:** Capacitor dopo lo scheletro (T21); per l'iPhone servono l'account Apple Developer e una compilazione su macchine macOS nel cloud; da decidere insieme alla pipeline (ADR 0008).
