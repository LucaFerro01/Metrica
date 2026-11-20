import createClient from 'openapi-fetch';
import type { paths as ActivitiesPaths } from './activities';
import { activitiesBaseUrl, API_BASE, ingestionBaseUrl } from './config';
import type { paths as IngestionPaths } from './ingestion';

// Due istanze di openapi-fetch, una per servizio, con i tipi generati da `pnpm gen:api`
// (REQ-029). Il web dialoga solo con il gateway (REQ-025). Le factory accettano la base dell'API
// (non quella del singolo servizio) per poter essere provate con un URL assoluto.

/**
 * Chiama sempre il `fetch` globale CORRENTE, non quello catturato alla creazione del client: così un test può
 * sostituirlo (`vi.stubGlobal('fetch', ...)`) anche dopo che il client di modulo è stato costruito.
 */
const globalFetch: typeof fetch = (input, init) => globalThis.fetch(input, init);

/**
 * Base relativa (`/api`) → assoluta rispetto all'origine della pagina. Nel browser è equivalente; serve dove
 * `new Request('/api/...')` non può risolvere un URL relativo (jsdom, Node). Una base già assoluta non cambia.
 */
export function resolveApiBase(base: string, origin: string | undefined = globalThis.location?.origin): string {
  if (!origin || origin === 'null' || /^[a-z][a-z\d+.-]*:/i.test(base)) return base;
  return new URL(base === '' ? '/' : base, origin).href.replace(/\/+$/, '');
}

export function createActivitiesClient(apiBase: string = API_BASE, fetchImpl: typeof fetch = globalFetch) {
  return createClient<ActivitiesPaths>({ baseUrl: resolveApiBase(activitiesBaseUrl(apiBase)), fetch: fetchImpl });
}

export function createIngestionClient(apiBase: string = API_BASE) {
  return createClient<IngestionPaths>({ baseUrl: ingestionBaseUrl(apiBase) });
}

export const activitiesClient = createActivitiesClient();
export const ingestionClient = createIngestionClient();
