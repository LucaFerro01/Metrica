# ADR 0006 · Contenitori neutri tra Docker e Podman

- **Stato:** Accettata
- **Data:** 2026-11-12

## Contesto

L'autore vuole usare **Docker o Podman** e non vuole legarsi a uno solo.

## Decisione

Il progetto usa solo funzioni **standard e portabili**: immagini OCI costruite da `Dockerfile`, orchestrazione locale con un unico `compose.yaml`, avviabile con `docker compose` o `podman compose`.

## Regole

- Nessun montaggio del socket di Docker, nessuna estensione esclusiva di un runtime.
- Nomi di immagine completi (con registro).
- Utente non privilegiato nei contenitori e compatibilità con Podman *rootless*.
- Volumi con attenzione a permessi ed etichette SELinux (`:Z` dove serve).
- Ogni modifica a `compose.yaml` si prova con entrambi i runtime (almeno a mano all'inizio).

## Motivazioni

- Portabilità e nessun vincolo di licenza o di prodotto.
- Podman è *rootless* e senza demone, interessante per la sicurezza.
- Un unico file di descrizione, minore manutenzione.

## Conseguenze

- Alcune funzioni di comodo di Docker Desktop non sono usabili.
- Differenze sottili (rete, permessi sui volumi, ordine di avvio con `depends_on`) da verificare con entrambi.
- Aspire, se adottato, può usare entrambi i runtime ma va verificato.

## Alternative considerate

- **Solo Docker:** più semplice, ma vincolante.
- **Solo Podman con Quadlet/`pod`:** potente, ma meno portabile.
- **Kubernetes in locale (kind, minikube):** troppo pesante per lo sviluppo quotidiano; rimane possibile per la produzione.
