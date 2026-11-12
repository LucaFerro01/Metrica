# ADR 0004 · PostgreSQL con TimescaleDB, uno schema per servizio

- **Stato:** Proposta
- **Data:** 2026-11-12

## Contesto

Servono dati relazionali (utenti, template, calendario) e grandi quantità di serie temporali (stream delle attività). Il PoC usava già PostgreSQL con TimescaleDB e Drizzle.

## Decisione

- **PostgreSQL** con estensione **TimescaleDB** per le serie temporali.
- **Un'istanza** in sviluppo, **uno schema per servizio** con utente dedicato e permessi solo sul proprio schema.
- Ogni servizio gestisce le proprie migrazioni.
- Accesso ai dati: **EF Core** per impostazione predefinita; Dapper/SQL diretto dove serve controllo sulle prestazioni (stream, analisi). *Decisione aperta.*

## Motivazioni

- Un solo motore per due tipi di dato riduce la complessità operativa.
- Isolamento imposto dal database: un servizio non può leggere lo schema di un altro.
- Timescale offre compressione e aggregati continui, utili per stream e riepiloghi.
- Si può passare a istanze separate per servizio cambiando solo la stringa di connessione.

## Conseguenze

- Istanza unica = un solo punto di guasto in produzione; accettabile per questa fase.
- Servizi che condividono l'istanza possono influenzarsi nelle prestazioni (carico dell'analisi sull'archivio).
- Necessario curare permessi e script di inizializzazione per rendere reale l'isolamento.

## Alternative considerate

- **Un database per servizio fin dall'inizio:** isolamento massimo, ma più peso in locale. Possibile in seguito.
- **Un database specializzato per le serie temporali (InfluxDB, ClickHouse):** più motori da gestire; non necessario per questi volumi.
- **Database per tipo (documentale per gli stream):** meno adatto alle query sugli intervalli e ai riepiloghi.
