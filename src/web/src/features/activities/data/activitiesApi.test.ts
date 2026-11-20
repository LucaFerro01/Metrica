import { describe, expect, it } from 'vitest';
import {
  ActivitiesRequestError,
  DETAIL_MAX_POINTS,
  fetchActivityDetail,
  fetchActivityExists,
  fetchActivityList,
  isActivityId,
  PAGE_SIZE,
} from './activitiesApi';
import { activityId, jsonResponse, listBody, makeDetail, makeItem, stubActivitiesApi } from '@/test/fakeActivitiesApi';

async function kindOf(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (error) {
    expect(error).toBeInstanceOf(ActivitiesRequestError);
    return (error as ActivitiesRequestError).kind;
  }
  throw new Error('attesa un\'eccezione');
}

describe('richieste a activities', () => {
  it('elenco: pagina, pageSize esplicito e sport solo se presente', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)])));
    await fetchActivityList({ sport: undefined, page: 1 });
    await fetchActivityList({ sport: 'ride', page: 4 });
    const [first, second] = api.urls();
    expect(PAGE_SIZE).toBe(20);
    expect(first?.searchParams.get('pageSize')).toBe('20');
    expect(first?.searchParams.has('sport')).toBe(false);
    expect(second?.searchParams.get('sport')).toBe('ride');
    expect(second?.searchParams.get('page')).toBe('4');
  });

  it('rilevamento: GET /activities con externalId esatto e pageSize=1, senza sport né pagina successiva', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([])));
    expect(await fetchActivityExists('ab'.repeat(32))).toBe(false);
    const [url] = api.urls();
    expect(url?.pathname).toBe('/api/activities');
    expect(url?.searchParams.get('externalId')).toBe('ab'.repeat(32));
    expect(url?.searchParams.get('pageSize')).toBe('1');
    expect(url?.searchParams.has('sport')).toBe(false);
  });

  it('rilevamento: true se il server restituisce almeno un\'attività', async () => {
    stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)], { total: 1 })));
    expect(await fetchActivityExists('c'.repeat(64))).toBe(true);
  });

  it(`dettaglio: GET /activities/{id}?maxPoints=${DETAIL_MAX_POINTS} (il minimo ammesso, nessun punto inutile)`, async () => {
    const api = stubActivitiesApi(() => jsonResponse(makeDetail(1)));
    await fetchActivityDetail(activityId(1));
    const [url] = api.urls();
    expect(DETAIL_MAX_POINTS).toBe(2);
    expect(url?.pathname).toBe(`/api/activities/${activityId(1)}`);
    expect(url?.searchParams.get('maxPoints')).toBe('2');
  });
});

describe('errori', () => {
  it.each([
    [404, 'not-found'],
    [400, 'bad-request'],
    [500, 'server'],
    [502, 'server'],
    [504, 'server'],
    [401, 'unexpected'],
    [409, 'unexpected'],
  ])('stato %i → %s', async (status, kind) => {
    stubActivitiesApi(() => new Response(null, { status }));
    expect(await kindOf(fetchActivityList({ sport: undefined, page: 1 }))).toBe(kind);
    expect(await kindOf(fetchActivityDetail(activityId(1)))).toBe(kind);
  });

  it('rete assente e corpo non JSON → network; corpo vuoto o di forma sbagliata → unexpected', async () => {
    stubActivitiesApi(() => Promise.reject(new TypeError('Failed to fetch')));
    expect(await kindOf(fetchActivityList({ sport: undefined, page: 1 }))).toBe('network');
    stubActivitiesApi(() => new Response('<html>', { status: 200 }));
    expect(await kindOf(fetchActivityList({ sport: undefined, page: 1 }))).toBe('network');
    stubActivitiesApi(() => new Response(null, { status: 200 }));
    expect(await kindOf(fetchActivityList({ sport: undefined, page: 1 }))).toBe('unexpected');
    stubActivitiesApi(() => jsonResponse({ items: [{ nome: 'x' }], total: 1 }));
    expect(await kindOf(fetchActivityList({ sport: undefined, page: 1 }))).toBe('unexpected');
    stubActivitiesApi(() => jsonResponse(['non un oggetto']));
    expect(await kindOf(fetchActivityDetail(activityId(1)))).toBe('unexpected');
  });

  it('il messaggio dell\'errore non contiene mai testo del server né l\'URL', async () => {
    stubActivitiesApi(() => jsonResponse({ detail: 'SEGRETO', title: 'SEGRETO' }, 500));
    // `rejects` fallisce se la promessa NON viene rifiutata (un try/catch passerebbe a vuoto).
    const failure = await fetchActivityDetail(activityId(1)).then(
      () => undefined,
      (error: unknown) => error,
    );
    expect(failure).toBeInstanceOf(ActivitiesRequestError);
    expect((failure as Error).message).not.toMatch(/SEGRETO|activities|http/);
    await expect(fetchActivityDetail(activityId(1))).rejects.toMatchObject({ kind: 'server' });
  });
});

describe('isActivityId', () => {
  it.each([activityId(1), 'f47ac10b-58cc-4372-a567-0e02b2c3d479', 'F47AC10B-58CC-4372-A567-0E02B2C3D479'])('%s è valido', (id) => {
    expect(isActivityId(id)).toBe(true);
  });

  it.each(['', 'abc-123', 'f47ac10b58cc4372a5670e02b2c3d479', `${activityId(1)}x`, ` ${activityId(1)}`, '../etc', activityId(1).replace(/-/g, '_')])(
    '%j non è valido',
    (id) => {
      expect(isActivityId(id)).toBe(false);
    },
  );
});
