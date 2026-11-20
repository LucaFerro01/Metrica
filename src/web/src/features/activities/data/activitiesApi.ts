import { activitiesClient } from '@/shared/api/client';
import type { components } from '@/shared/api/activities';
import type { Sport } from './sports';

// Accesso ai dati di `activities` (solo lettura) e chiavi di TanStack Query.
//
// Ogni funzione restituisce i dati validati oppure lancia `ActivitiesRequestError` con un `kind` che la UI sa
// tradurre: mai un testo del server, mai l'identificativo o l'URL nei messaggi. Gli errori del gateway (404,
// 502, 504, 400 di Kestrel) hanno il corpo VUOTO: si decide solo dallo stato HTTP.

export type ActivityListResponse = components['schemas']['ActivityListPage'];
export type ActivityListItem = components['schemas']['ActivityListItem'];
export type ActivityDetail = components['schemas']['ActivityDetail'];

/** Dimensione della pagina dell'elenco (esplicita: il predefinito del server non è un contratto del web). */
export const PAGE_SIZE = 20;

/**
 * Punti dello stream richiesti con il dettaglio: il web non mostra grafici né mappe, quindi il minimo
 * ammesso dal server (2) invece dei 1000 predefiniti.
 */
export const DETAIL_MAX_POINTS = 2;

export type RequestErrorKind = 'network' | 'bad-request' | 'not-found' | 'server' | 'unexpected';

export class ActivitiesRequestError extends Error {
  readonly kind: RequestErrorKind;
  readonly status: number | undefined;

  constructor(kind: RequestErrorKind, status?: number) {
    super(kind);
    this.name = 'ActivitiesRequestError';
    this.kind = kind;
    this.status = status;
  }
}

function kindForStatus(status: number): RequestErrorKind {
  if (status === 404) return 'not-found';
  if (status === 400) return 'bad-request';
  if (status >= 500) return 'server';
  return 'unexpected';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isActivityList(value: unknown): value is ActivityListResponse {
  return (
    isRecord(value) &&
    typeof value.total === 'number' &&
    Number.isFinite(value.total) &&
    Array.isArray(value.items) &&
    value.items.every((item) => isRecord(item) && typeof item.id === 'string')
  );
}

export function isActivityDetail(value: unknown): value is ActivityDetail {
  return isRecord(value) && typeof value.id === 'string';
}

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** `true` se l'identificativo ha la forma di un UUID: altrimenti non serve nemmeno chiedere al server. */
export function isActivityId(value: string): boolean {
  return UUID.test(value);
}

interface RawResult {
  data?: unknown;
  response: Response;
}

async function request<T>(send: () => Promise<RawResult>, isValid: (data: unknown) => data is T): Promise<T> {
  let result: RawResult;
  try {
    result = await send();
  } catch {
    // Rete assente, richiesta annullata, corpo 200 non JSON: per l'utente è lo stesso problema.
    throw new ActivitiesRequestError('network');
  }
  const { data, response } = result;
  if (!response.ok) throw new ActivitiesRequestError(kindForStatus(response.status), response.status);
  if (!isValid(data)) throw new ActivitiesRequestError('unexpected', response.status);
  return data;
}

export interface ListParams {
  sport: Sport | undefined;
  page: number;
}

export function fetchActivityList({ sport, page }: ListParams, signal?: AbortSignal): Promise<ActivityListResponse> {
  return request(
    () =>
      activitiesClient.GET('/activities', {
        // `sport` assente = nessun filtro: il server rifiuta `sport=` vuoto.
        params: { query: { ...(sport ? { sport } : {}), page, pageSize: PAGE_SIZE } },
        signal,
      }),
    isActivityList,
  );
}

/**
 * Esiste un'attività dell'utente con questo `externalId` (DEC-017)? Interroga il server con il filtro esatto
 * (`pageSize=1`): non cerca nella pagina visibile, che per un file vecchio non la conterrebbe.
 */
export async function fetchActivityExists(externalId: string, signal?: AbortSignal): Promise<boolean> {
  const list = await request(
    () => activitiesClient.GET('/activities', { params: { query: { externalId, pageSize: 1 } }, signal }),
    isActivityList,
  );
  return list.items.length > 0;
}

export function fetchActivityDetail(id: string, signal?: AbortSignal): Promise<ActivityDetail> {
  return request(
    () =>
      activitiesClient.GET('/activities/{id}', {
        params: { path: { id }, query: { maxPoints: DETAIL_MAX_POINTS } },
        signal,
      }),
    isActivityDetail,
  );
}

export const activityKeys = {
  all: ['activities'] as const,
  lists: () => [...activityKeys.all, 'list'] as const,
  list: ({ sport, page }: ListParams) => [...activityKeys.lists(), { sport: sport ?? null, page }] as const,
  detail: (id: string) => [...activityKeys.all, 'detail', id] as const,
  byExternalId: (externalId: string) => [...activityKeys.all, 'by-external-id', externalId] as const,
};
