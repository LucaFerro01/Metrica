-- Eseguito automaticamente SOLO alla prima creazione del volume del database
-- (l'immagine ignora gli script se i dati esistono già): dopo ogni modifica di
-- questo file serve "docker compose -f deploy/compose.yaml down -v" e un nuovo "up -d"
-- (cancella i dati di sviluppo).
-- Crea uno schema e un utente per ciascun servizio: ogni servizio può
-- accedere SOLO al proprio schema (vedi ARCHITECTURE/03-data.md).
-- Le password sono valori di SOLO SVILUPPO.

CREATE EXTENSION IF NOT EXISTS timescaledb;

DO $$
DECLARE
    svc text;
BEGIN
    FOREACH svc IN ARRAY ARRAY['identity', 'ingestion', 'activities', 'analytics', 'training_plan']
    LOOP
        EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L', svc || '_svc', 'dev_only_' || svc);
        EXECUTE format('CREATE SCHEMA %I AUTHORIZATION %I', svc, svc || '_svc');
        -- DEC-014: l'estensione timescaledb risiede in "public"; senza USAGE su "public" e senza
        -- "public" nel search_path create_hypertable() non si trova o dà "permission denied".
        -- Il ruolo NON riceve CREATE su "public".
        EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', svc || '_svc');
        EXECUTE format('ALTER ROLE %I SET search_path = %I, public', svc || '_svc', svc);
    END LOOP;
END
$$;

-- Nessun accesso di default allo schema pubblico: solo i ruoli di servizio ricevono USAGE (sopra),
-- mai CREATE.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
