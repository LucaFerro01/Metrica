-- Eseguito automaticamente al primo avvio del contenitore PostgreSQL.
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
        EXECUTE format('ALTER ROLE %I SET search_path = %I', svc || '_svc', svc);
    END LOOP;
END
$$;

-- Nessun accesso di default allo schema pubblico per gli utenti dei servizi.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
