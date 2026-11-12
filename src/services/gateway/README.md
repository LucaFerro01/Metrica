# gateway

Punto d'ingresso unico verso i servizi (YARP). **Non contiene logica di business né database.**

- **Responsabilità:** routing `/api/<servizio>/...`, validazione dei token, rate limiting, CORS, inoltro del webhook di Strava a `ingestion`.
- **Dati posseduti:** nessuno.
- **Eventi:** nessuno.
- **Stato:** da implementare.

Vedi [`ARCHITECTURE/01-services.md`](../../../ARCHITECTURE/01-services.md).
