import { vi, type Mock } from 'vitest';
import type { components } from '@/shared/api/activities';

// API `activities` simulata per i test del web: sostituisce il `fetch` globale (il client generato lo legge a
// ogni richiesta) e registra gli URL chiamati. Nessuna rete, nessun server vero.

export type ListItem = components['schemas']['ActivityListItem'];
export type Detail = components['schemas']['ActivityDetail'];

/** UUID deterministico per numero (versione 4, variante RFC). */
export function activityId(n: number): string {
  return `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
}

/** `externalId` (SHA-256 minuscolo, 64 caratteri) deterministico per numero. */
export function externalIdOf(n: number): string {
  return n.toString(16).padStart(64, 'a');
}

export function makeItem(n: number, overrides: Partial<ListItem> = {}): ListItem {
  return {
    id: activityId(n),
    externalId: externalIdOf(n),
    sport: 'run',
    startTime: '2026-11-19T07:30:00+00:00',
    distanceMeters: 12345,
    elapsedSeconds: 3725,
    elevationGainMeters: 123,
    ...overrides,
  };
}

export function makeDetail(n: number, overrides: Partial<Detail> = {}): Detail {
  return {
    ...makeItem(n),
    movingSeconds: 3600,
    summary: { avgHeartRate: 151.4, maxHeartRate: 180, avgPowerWatts: 212.6, avgCadence: 84 },
    laps: [],
    stream: {
      timeOffsetSeconds: [0, 3725],
      latitude: null,
      longitude: null,
      altitudeMeters: null,
      heartRate: null,
      powerWatts: null,
      cadence: null,
      speedMetersPerSecond: null,
    },
    ...overrides,
  };
}

export function listBody(items: ListItem[], { page = 1, total = items.length, pageSize = 20 } = {}) {
  return { items, page, pageSize, total };
}

export function jsonResponse(body: unknown, status = 200): Response {
  return Response.json(body, { status });
}

export interface FakeApi {
  fetch: Mock<(input: Request) => Promise<Response>>;
  /** Tutti gli URL richiesti finora, in ordine. */
  urls: () => URL[];
  /** Solo le richieste all'elenco (`/api/activities`) con il filtro `externalId` (rilevamento). */
  externalIdRequests: (externalId?: string) => URL[];
  /** Le richieste all'elenco senza `externalId`. */
  listRequests: () => URL[];
}

/** Sostituisce `fetch`: `handler` riceve l'URL (assoluto, base della pagina di test) e risponde. */
export function stubActivitiesApi(handler: (url: URL) => Response | Promise<Response>): FakeApi {
  const fetchMock = vi.fn(async (request: Request) => handler(new URL(request.url)));
  vi.stubGlobal('fetch', fetchMock);
  const urls = () => fetchMock.mock.calls.map(([request]) => new URL(request.url));
  return {
    fetch: fetchMock,
    urls,
    externalIdRequests: (externalId) =>
      urls().filter(
        (url) =>
          url.pathname === '/api/activities' &&
          url.searchParams.has('externalId') &&
          (externalId === undefined || url.searchParams.get('externalId') === externalId),
      ),
    listRequests: () =>
      urls().filter((url) => url.pathname === '/api/activities' && !url.searchParams.has('externalId')),
  };
}

/** Una promessa risolvibile dall'esterno: per tenere una risposta «in volo» e vedere lo stato di caricamento. */
export function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}
