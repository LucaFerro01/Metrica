---
schema_version: 1
kind: feature
key: walking-skeleton-import
title: "Scheletro del sistema e import di file GPX/FIT"
branch: "feature/walking-skeleton-import"
integration_branch: null
base_commit: null
workspace_mode: primary
status: implementation
created_at: 2026-11-12
---

# Scheletro del sistema e import di file GPX/FIT

## Goal
Preparare solution .NET e ServiceDefaults, poi importare un file GPX/FIT da ingestion, pubblicare ActivityImported su NATS, salvarlo in activities, leggerlo via gateway YARP e mostrarlo nel web React

## Sessions
- 2026-11-12 — development initialized
- 2026-11-12 — requirements drafted (REQ-001…034, AC-001…013); readiness gate passed, status → planning; approvati dall'utente
- 2026-11-12 — pianificazione: decisioni DEC-001…DEC-014 registrate (DEC-001…012 discusse una alla volta, DEC-012…014 anche dopo la revisione); requisiti estesi con REQ-035/036 e AC-014 (tetto di 100.000 punti, evento sotto 8 MB) e vincolo sul codice condiviso (`Messaging`); piano di 20 task scritto e rivisto due volte da un revisore indipendente; esperimento sul database di sviluppo: `create_hypertable` richiede `GRANT USAGE ON SCHEMA public` ai ruoli di servizio (DEC-014); piano marcato `ready`, status → implementation
- Nota: i file creati da PFlow e dalla documentazione non sono ancora committati (commit policy `manual`)
- 2026-11-12 — T01 completato (modalità inline, tier `light`): `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `TriMetric.slnx` vuota, `.config/dotnet-tools.json` (dotnet-ef 10.0.15). Verificato con un progetto di prova temporaneo (poi rimosso): SDK 11.0.100 compila per `net10.0` con tutti i pacchetti centralizzati a 0 warning con `-warnaserror`, esecuzione su .NET 10.0.15. Deviazioni dal piano: (1) aggiunto `nuget.config` solo con nuget.org: la macchina ha un secondo feed aziendale e la gestione centralizzata dei pacchetti con warning-come-errori dava NU1507; (2) `dotnet build TriMetric.slnx` su solution vuota emette il warning "Unable to find a project to restore" (inevitabile finché non ci sono progetti; sparisce con T02); (3) `xunit.runner.visualstudio` fissato a 3.1.5 (la 4.x è per xunit v3; riconfermato funzionante in T02). Checkpoint: `48b4859`
- 2026-11-12 — T02 completato (agente separato, tier `strong`, revisione indipendente: spec **Approved**, qualità **Approved**, nessun bloccante): `TriMetric.Contracts` con busta, `ActivityImported`, `StreamColumns`, `EventNames` (stream `EVENTS` con prefissi espliciti e `DLQ`), `StreamLimits`, `ContractJson.Options` e 13 test; caso peggiore misurato 6.400.842 byte (64 B/punto, limite 8.388.608). Verificato anche dal controller: build 0 warning con `-warnaserror`, 13/13 test. Note: `ContractJson.Options` usa `MakeReadOnly(populateMissingResolver: true)` (necessario su .NET 10); `dotnet sln add` fallisce con progetti in cartelle omonime → le voci della slnx si aggiungono a mano con cartelle logiche piatte `/src/` e `/tests/`; commenti con "e'" al posto di "è" (accettato, da uniformare a piacere). Aggiunte al piano da raccomandazioni del revisore: validazione esplicita dell'evento in T12 (colonne di lunghezza uguale, `Data` non nulla, `MaxPoints`, sport valido → DLQ) e, in T05, copia degli array dei subject prima di passarli a NATS.Net

## Requirements
- `requirements.md` — 34 REQ e 13 AC; ambito: setup .NET, ingestion (GPX/FIT), activities, gateway YARP, web React; fuori ambito: Strava, identity, analytics, training-plan, Docker dei servizi, PWA.

## Plans
- `plan.md` — pronto: 20 task (T01–T20) sequenziali, verificati da due passaggi di revisione indipendente. Decisioni vincolanti in `decisions.md` (DEC-001…DEC-014, tutte confermate dall'utente). Azioni umane previste: `down -v` del volume Postgres (T04), attivare `pnpm` e scaricare i browser di Playwright (T16), approvare le migrazioni (T09, T11).
