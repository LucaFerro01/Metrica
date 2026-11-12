# identity

Utenti, sessioni, token e collegamenti OAuth ai servizi esterni.

- **Responsabilità:** registrazione e accesso (Strava, poi email/password), emissione e rotazione dei token, conservazione cifrata dei token Strava/Zepp, esportazione e cancellazione dei dati.
- **Dati posseduti:** schema `identity` (utenti, sessioni, collegamenti).
- **Pubblica:** `UserRegistered`, `ConnectionLinked`, `ConnectionRevoked`, `UserDeleted`.
- **Consuma:** nessuno.
- **Decisione aperta:** ASP.NET Core Identity + OpenIddict oppure provider esterno ([`06-security.md`](../../../ARCHITECTURE/06-security.md)).
- **Stato:** da implementare.
