# ADR 0007 · Micro-frontend: non adottati all'inizio

- **Stato:** Proposta
- **Data:** 2026-11-12

## Contesto

L'autore vuole capire se l'architettura a microservizi abbia senso anche sul frontend. Lavora da solo.

## Decisione

Si parte con **un'unica applicazione React organizzata per funzione** (`features/activities`, `analytics`, `planning`, `profile`, `auth`), con confini netti e comunicazione solo tramite un livello `shared/`. I micro-frontend **non** si adottano ora.

## Motivazioni

- Il vantaggio dei micro-frontend è principalmente **organizzativo** (team che rilasciano in autonomia). Con un solo sviluppatore non esiste.
- Costi concreti: dipendenze condivise, coerenza di stile e navigazione, build più complesse, test end-to-end difficili, rischio per le prestazioni su mobile.
- L'organizzazione per funzione rende la scissione futura poco costosa.

## Conseguenze

- Regola di lint che vieta importazioni dirette tra funzioni.
- Ogni funzione espone solo un'interfaccia pubblica.
- Se si vuole **imparare** la tecnica, si può fare un esperimento isolato su una sola area (es. l'editor di allenamenti) senza rendere obbligatorio il modello.

## Quando rivalutare

- Più persone lavorano sulla UI.
- Un'area ha un ritmo di rilascio molto diverso dalle altre.
- Si decide di dedicare una parte del progetto all'apprendimento di Module Federation o dei web component.

## Alternative considerate

- **Module Federation (Vite/Webpack):** carica moduli remoti a runtime; flessibile, ma complesso da tenere allineato.
- **Web component per area:** neutrali rispetto al framework, ma richiedono disciplina su stato e routing.
- **Applicazioni separate su percorsi diversi dietro il gateway:** la forma più semplice, ma con ricaricamento completo tra aree e stile duplicato.

## Esito dello scheletro del sistema (2026-11-20)

Sezione aggiornata con l'esito dello scheletro, come previsto dal piano T20 (verifica finale e aggiornamento dell'architettura). Nessun cambio di stato.

La struttura per funzione e la regola di lint esistono in `src/web` (`pnpm lint` impone che una feature non importi da un'altra né da `app`, che `shared` non importi da `features` o `app` e che da fuori si importi solo dall'`index.ts` della feature; un test prova le regole con la vera API di ESLint). Oggi c'è una sola feature, `activities`, quindi la scissione non è stata messa alla prova: la decisione resta *Proposta* e va rivalutata con altre aree.
