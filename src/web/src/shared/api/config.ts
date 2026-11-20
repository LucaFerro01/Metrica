// Configurazione della base URL dell'API (DEC-007, DEC-015, REQ-038).
//
// `VITE_API_BASE_URL` (in `.env.example`) è la base vista dal browser; predefinita `/api`, che il dev
// server di Vite inoltra al gateway. In un guscio nativo o con un gateway su un'altra origine si usa
// un URL assoluto. Da questa base derivano le basi dei due client:
//   activities: <base>            percorsi OpenAPI /activities e /activities/{id}   (gateway: /api/activities → /activities)
//   ingestion:  <base>/ingestion  percorso OpenAPI /uploads                          (gateway: /api/ingestion/uploads → /uploads)

export const DEFAULT_API_BASE = '/api';

/** Toglie spazi e barre finali; vuoto/assente → `/api`. `/` (API servita dalla radice) diventa la base vuota. */
export function normalizeApiBase(raw: string | undefined): string {
  const trimmed = (raw ?? '').trim();
  if (trimmed === '') return DEFAULT_API_BASE;
  return trimmed.replace(/\/+$/, '');
}

export function activitiesBaseUrl(apiBase: string): string {
  return apiBase;
}

export function ingestionBaseUrl(apiBase: string): string {
  return `${apiBase}/ingestion`;
}

export const API_BASE = normalizeApiBase(import.meta.env.VITE_API_BASE_URL);
export const ACTIVITIES_BASE_URL = activitiesBaseUrl(API_BASE);
export const INGESTION_BASE_URL = ingestionBaseUrl(API_BASE);
