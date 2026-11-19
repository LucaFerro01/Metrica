import { describe, expect, it, vi } from 'vitest';
import { createActivitiesClient, createIngestionClient } from './client';

// In jsdom (Node) `Request` richiede un URL assoluto: i test usano una base assoluta finta.
// Nel browser la base relativa `/api` si risolve rispetto all'origine della pagina.
const BASE = 'http://gateway.test/api';

function fakeFetch(body: unknown = {}, status = 200) {
  return vi.fn<(request: Request) => Promise<Response>>(async () => Response.json(body, { status }));
}

function lastUrl(fetchMock: ReturnType<typeof fakeFetch>): string {
  const request = fetchMock.mock.calls.at(-1)?.[0];
  if (!request) throw new Error('nessuna richiesta inviata');
  return request.url;
}

describe('client activities', () => {
  it('elenco: GET <base>/activities', async () => {
    const fetchMock = fakeFetch({ items: [], total: 0 });
    const client = createActivitiesClient(BASE);
    await client.GET('/activities', { fetch: fetchMock });
    expect(lastUrl(fetchMock)).toBe('http://gateway.test/api/activities');
  });

  it('dettaglio: GET <base>/activities/{id}', async () => {
    const fetchMock = fakeFetch({});
    const client = createActivitiesClient(BASE);
    await client.GET('/activities/{id}', {
      params: { path: { id: '3f2504e0-4f89-41d3-9a0c-0305e82c3301' } },
      fetch: fetchMock,
    });
    expect(lastUrl(fetchMock)).toBe('http://gateway.test/api/activities/3f2504e0-4f89-41d3-9a0c-0305e82c3301');
  });

  it('il filtro per sport finisce nella query', async () => {
    const fetchMock = fakeFetch({ items: [], total: 0 });
    const client = createActivitiesClient(BASE);
    await client.GET('/activities', { params: { query: { sport: 'run' } }, fetch: fetchMock });
    expect(new URL(lastUrl(fetchMock)).searchParams.get('sport')).toBe('run');
  });
});

describe('client ingestion', () => {
  it('upload: POST <base>/ingestion/uploads', async () => {
    const fetchMock = fakeFetch({}, 202);
    const client = createIngestionClient(BASE);
    const body = new FormData();
    body.append('file', new Blob(['<gpx/>']), 'run.gpx');
    await client.POST('/uploads', {
      body: body as never,
      bodySerializer: (b) => b as unknown as FormData,
      fetch: fetchMock,
    });
    expect(lastUrl(fetchMock)).toBe('http://gateway.test/api/ingestion/uploads');
    expect(fetchMock.mock.calls.at(-1)?.[0].method).toBe('POST');
  });
});
