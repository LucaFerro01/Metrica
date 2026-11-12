# ADR 0001 · Backend in C# / .NET

- **Stato:** Accettata
- **Data:** 2026-11-12

## Contesto

Il PoC usa Node.js con TypeScript ed Express. Il nuovo progetto riparte da zero e uno degli scopi è sviluppare competenze su C# e sui microservizi.

## Decisione

Il backend è scritto in **C# su .NET** (ultima versione LTS disponibile), con **ASP.NET Core minimal API** per i servizi HTTP e **YARP** per il gateway.

## Motivazioni

- Scelta dell'autore, con obiettivo di apprendimento esplicito.
- Prestazioni elevate e basso consumo di memoria, adatto a molti servizi piccoli.
- Ecosistema completo: EF Core, OpenTelemetry, health checks, Aspire, Testcontainers, client NATS ufficiale.
- Tipizzazione forte e buon supporto degli strumenti, anche di IA.

## Conseguenze

- Frontend e backend usano linguaggi diversi: i tipi del client si **generano** dall'OpenAPI per restare allineati.
- Il codice del PoC (TypeScript) non è riutilizzabile direttamente: la logica utile (matching, metriche) va riscritta, usando il PoC come specifica e i suoi test come casi di prova.
- Si fissa la versione di .NET in un `global.json` e si aggiorna ad ogni LTS.

## Alternative considerate

- **Rimanere su Node/TypeScript:** un solo linguaggio, ma contrario all'obiettivo di apprendimento.
- **Go, Rust:** leggeri e veloci, ma fuori dalla scelta dell'autore.
