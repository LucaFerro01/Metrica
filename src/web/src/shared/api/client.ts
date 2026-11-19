import createClient from 'openapi-fetch';
import type { paths as ActivitiesPaths } from './activities';
import { activitiesBaseUrl, API_BASE, ingestionBaseUrl } from './config';
import type { paths as IngestionPaths } from './ingestion';

// Due istanze di openapi-fetch, una per servizio, con i tipi generati da `pnpm gen:api`
// (REQ-029). Il web dialoga solo con il gateway (REQ-025). Le factory accettano la base dell'API
// (non quella del singolo servizio) per poter essere provate con un URL assoluto.

export function createActivitiesClient(apiBase: string = API_BASE) {
  return createClient<ActivitiesPaths>({ baseUrl: activitiesBaseUrl(apiBase) });
}

export function createIngestionClient(apiBase: string = API_BASE) {
  return createClient<IngestionPaths>({ baseUrl: ingestionBaseUrl(apiBase) });
}

export const activitiesClient = createActivitiesClient();
export const ingestionClient = createIngestionClient();
