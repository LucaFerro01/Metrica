# ADR 0002 · Architettura a microservizi

- **Stato:** Accettata
- **Data:** 2026-11-12

## Contesto

Un solo sviluppatore. Il PoC era un'applicazione con API e worker (due processi). L'autore vuole **sviluppare competenze su microservizi**, e per questo li sceglie anche se un monolite sarebbe più semplice.

## Decisione

Si adotta un'architettura a **microservizi** con questi servizi iniziali: `gateway`, `identity`, `ingestion`, `activities`, `analytics`, `training-plan`. Ogni servizio possiede i propri dati; la comunicazione è via HTTP (client → gateway → servizio) ed eventi (servizio ↔ servizio).

## Motivazioni

- Obiettivo dichiarato di apprendimento: eventi, idempotenza, osservabilità distribuita, deploy indipendenti.
- I confini del dominio sono naturali: ingestione, archivio, calcolo, pianificazione hanno ritmi e carichi diversi (l'ingestione è a raffica, l'analisi è pesante in CPU, la pianificazione è leggera).
- Isolare le integrazioni instabili (Zepp non ufficiale) in un servizio sostituibile.

## Conseguenze (costi accettati)

- **Complessità operativa** più alta di un monolite: più processi, rete, versioni, debug distribuito.
- **Consistenza eventuale** tra servizi: la UI deve gestire stati intermedi.
- **Ambienti di sviluppo più pesanti:** mitigati da compose e Aspire.
- **Test d'integrazione** più importanti.

## Come limitare il costo (per un solo sviluppatore)

1. **Pochi servizi, confini larghi.** Non scendere a nano-servizi; si divide solo dove c'è un motivo.
2. **Nessun codice condiviso** oltre ai contratti degli eventi.
3. **Un solo repository** (monorepo) con pipeline per servizio.
4. **Modello interno uniforme** a tutti i servizi, per non reinventarlo ogni volta.
5. **Prima l'infrastruttura comune** (osservabilità, health check, eventi con outbox), poi i servizi.
6. Servizi senza logica di dominio (`gateway`, `identity` all'inizio) restano piccoli.

## Alternative considerate

- **Monolite modulare** (stessi moduli, un solo processo): più semplice e quasi sempre la scelta migliore per un singolo sviluppatore. Scartato per l'obiettivo di apprendimento; resta come **piano di ripiego** se il costo diventa insostenibile: i moduli sono già separati per dati ed eventi, quindi l'unione sarebbe praticabile.
- **Pochi servizi grandi** (es. solo `api` + `worker`, come il PoC): meno apprendimento sui temi distribuiti.
