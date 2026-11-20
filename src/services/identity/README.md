# identity

Utenti, sessioni, token e collegamenti OAuth ai servizi esterni.

- **Responsabilità:** registrazione e accesso (Strava, poi email/password), emissione e rotazione dei token, conservazione cifrata dei token Strava/Zepp, esportazione e cancellazione dei dati.
- **Dati posseduti:** schema `identity` (utenti, sessioni, collegamenti).
- **Pubblica:** `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted`.
- **Consuma:** nessuno.
- **Decisione aperta:** ASP.NET Core Identity + OpenIddict oppure provider esterno ([`06-security.md`](../../../ARCHITECTURE/06-security.md)).
- **Stato:** previsto, non implementato: nessun progetto .NET, nessuna migrazione, nessun evento definito in `contracts`. Esistono solo questo README e due predisposizioni comuni: `deploy/postgres/init/01-schemas.sql` crea già il ruolo `identity_svc` e lo schema `identity`, e `EventNames.EventsStreamSubjects` riserva il prefisso `identity.>` nello stream `EVENTS`.
