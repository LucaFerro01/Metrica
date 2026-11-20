import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { i18n } from '@/shared/i18n';
import { formatDateTime } from '@/shared/lib/format';
import {
  activityId,
  deferred,
  jsonResponse,
  makeDetail,
  stubActivitiesApi,
  type Detail,
} from '@/test/fakeActivitiesApi';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { renderApp } from '@/test/renderApp';
import { resetPersistedPreferences } from '@/test/resetPreferences';

const NBSP = ' ';
const ID = activityId(7);
const START = '2026-11-19T07:30:00+00:00';

let disposeTheme: (() => void) | undefined;

async function mount(path = `/activities/${ID}`) {
  const result = await renderApp(path);
  disposeTheme = result.disposeTheme;
  return result;
}

beforeEach(() => {
  resetPersistedPreferences();
  document.documentElement.classList.remove('dark');
  installFakeMatchMedia(false);
});

afterEach(async () => {
  disposeTheme?.();
  disposeTheme = undefined;
  await i18n.changeLanguage('it');
});

/** Il valore mostrato sotto un'etichetta del riepilogo (`dt` → `dd` successivo). */
function stat(label: string): HTMLElement {
  const term = screen.getByText(label, { selector: 'dt' });
  const value = term.nextElementSibling;
  if (!(value instanceof HTMLElement)) throw new Error(`nessun valore per ${label}`);
  return value;
}

function fullDetail(overrides: Partial<Detail> = {}): Detail {
  return makeDetail(7, {
    id: ID,
    sport: 'ride',
    startTime: START,
    distanceMeters: 42195,
    elapsedSeconds: 7384,
    movingSeconds: 7001,
    elevationGainMeters: 456.4,
    ...overrides,
  });
}

describe('dettaglio: riepilogo', () => {
  it('mostra data e ora, sport, distanza, durata, durata in movimento, dislivello, FC, potenza e cadenza', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount();
    expect(await screen.findByRole('heading', { level: 1, name: 'Bici' })).toBeInTheDocument();
    expect(screen.getByText(formatDateTime(START, 'it') ?? 'x')).toBeInTheDocument();

    expect(stat('Distanza')).toHaveTextContent(`42,20 km`);
    expect(stat('Durata')).toHaveTextContent('2:03:04');
    expect(stat('Durata in movimento')).toHaveTextContent('1:56:41');
    expect(stat('Dislivello')).toHaveTextContent(`456 m`);
    expect(stat('FC media')).toHaveTextContent(`151 bpm`);
    expect(stat('Potenza media')).toHaveTextContent(`213 W`);
    expect(stat('Cadenza media')).toHaveTextContent(`84 rpm`);
    expect(screen.queryByText('non disponibile')).not.toBeInTheDocument();
  });

  it('i numeri sono grandi e leggibili (classi di testo grandi) e il riepilogo ha un titolo per gli screen reader', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(stat('Distanza')).toHaveClass('text-2xl', 'font-semibold');
    expect(screen.getByRole('heading', { level: 2, name: 'Riepilogo' })).toBeInTheDocument();
  });

  it('i campi nulli mostrano «non disponibile» (mai 0 o vuoto)', async () => {
    stubActivitiesApi(() =>
      jsonResponse(
        fullDetail({
          elevationGainMeters: null,
          summary: { avgHeartRate: null, maxHeartRate: null, avgPowerWatts: null, avgCadence: null },
        }),
      ),
    );
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    for (const label of ['Dislivello', 'FC media', 'Potenza media', 'Cadenza media']) {
      expect(stat(label)).toHaveTextContent('non disponibile');
      expect(stat(label)).not.toHaveTextContent(/\d/);
    }
    // I campi presenti restano al loro posto.
    expect(stat('Distanza')).toHaveTextContent(`42,20 km`);
    expect(screen.getAllByText('non disponibile')).toHaveLength(4);
  });

  it('un riepilogo del tutto assente non provoca errori', async () => {
    stubActivitiesApi(() => jsonResponse({ ...fullDetail(), summary: undefined, laps: undefined }));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.getAllByText('non disponibile')).toHaveLength(3);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Giri' })).not.toBeInTheDocument();
  });

  it('uno sport sconosciuto si mostra come «Altro»', async () => {
    stubActivitiesApi(() => jsonResponse({ ...fullDetail(), sport: 'kayak' }));
    await mount();
    expect(await screen.findByRole('heading', { level: 1, name: 'Altro' })).toBeInTheDocument();
  });

  it('chiede il dettaglio con maxPoints=2 (nessun punto inutile) e nessun altro dato', async () => {
    const api = stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(api.urls()).toHaveLength(1);
    const [url] = api.urls();
    expect(url?.pathname).toBe(`/api/activities/${ID}`);
    expect(url?.searchParams.get('maxPoints')).toBe('2');
  });

  it('non mostra grafici, mappe né dati dello stream', async () => {
    stubActivitiesApi(() =>
      jsonResponse(
        fullDetail({
          stream: {
            timeOffsetSeconds: [0, 1],
            latitude: [45.1, 45.2],
            longitude: [7.5, 7.6],
            altitudeMeters: null,
            heartRate: [100, 101],
            powerWatts: null,
            cadence: null,
            speedMetersPerSecond: null,
          },
        }),
      ),
    );
    const { container } = await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(container.querySelector('canvas, svg[role="img"], img, iframe')).toBeNull();
    expect(container.textContent ?? '').not.toMatch(/45[.,]1|7[.,]5/);
  });
});

describe('dettaglio: giri', () => {
  const laps = [
    { startTime: START, elapsedSeconds: 600, distanceMeters: 2500 },
    { startTime: '2026-11-19T07:40:05+00:00', elapsedSeconds: 3725, distanceMeters: 850 },
  ];

  it('elenca i giri con inizio relativo, durata e distanza', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail({ laps })));
    await mount();
    expect(await screen.findByRole('heading', { level: 2, name: 'Giri' })).toBeInTheDocument();
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('columnheader').map((header) => header.textContent)).toEqual([
      'Giro',
      'Inizio',
      'Durata',
      'Distanza',
    ]);
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getAllByRole('cell').map((cell) => cell.textContent)).toEqual([
      '0:00',
      '10:00',
      `2,50${NBSP}km`,
    ]);
    expect(within(rows[0]!).getByRole('rowheader')).toHaveTextContent('Giro 1');
    // Inizio relativo: 07:40:05 - 07:30:00 = 10:05.
    expect(within(rows[1]!).getAllByRole('cell').map((cell) => cell.textContent)).toEqual([
      '10:05',
      '1:02:05',
      `850${NBSP}m`,
    ]);
    expect(within(rows[1]!).getByRole('rowheader')).toHaveTextContent('Giro 2');
  });

  it('un giro con campi nulli o istanti non validi mostra «non disponibile» senza errori', async () => {
    stubActivitiesApi(() =>
      jsonResponse(
        fullDetail({
          laps: [
            { startTime: 'non-una-data', elapsedSeconds: null, distanceMeters: null } as unknown as Detail['laps'][number],
          ],
        }),
      ),
    );
    await mount();
    await screen.findByRole('heading', { level: 2, name: 'Giri' });
    const row = within(screen.getByRole('table')).getAllByRole('row')[1]!;
    expect(within(row).getAllByText('non disponibile')).toHaveLength(3);
  });

  it('senza giri non c\'è la sezione', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail({ laps: [] })));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.queryByRole('heading', { name: 'Giri' })).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});

describe('dettaglio: assente, non valido, errori', () => {
  it('404: pagina «Attività non trovata» con il link all\'elenco', async () => {
    stubActivitiesApi(() => new Response(null, { status: 404 }));
    await mount();
    expect(await screen.findByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toHaveAttribute('href', '/activities');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Riprova' })).not.toBeInTheDocument();
  });

  it('404 con corpo ProblemDetails: stessa pagina, senza testo del server', async () => {
    stubActivitiesApi(() =>
      jsonResponse({ type: 'urn:metrica:problem:activity-not-found', detail: 'SEGRETO', status: 404 }, 404),
    );
    await mount();
    expect(await screen.findByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent('SEGRETO');
  });

  it.each(['abc-123', '12345', 'f47ac10b58cc4372a5670e02b2c3d479', `${ID}x`])(
    'id non valido %j: stessa pagina «non trovata» e nessuna richiesta di rete',
    async (id) => {
      const api = stubActivitiesApi(() => jsonResponse(fullDetail()));
      await mount(`/activities/${id}`);
      expect(await screen.findByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeInTheDocument();
      expect(screen.getByRole('link', { name: "Torna all'elenco" })).toBeInTheDocument();
      expect(api.fetch).not.toHaveBeenCalled();
    },
  );

  it('mostra lo stato di caricamento finché la risposta non arriva', async () => {
    const gate = deferred<Response>();
    stubActivitiesApi(() => gate.promise);
    await mount();
    expect(await screen.findByRole('status')).toHaveTextContent("Caricamento dell'attività…");
    gate.resolve(jsonResponse(fullDetail()));
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it.each([
    ['500', () => jsonResponse({ title: 'SEGRETO' }, 500), 'Il servizio non risponde in questo momento. Riprova tra poco.'],
    ['502 a corpo vuoto', () => new Response(null, { status: 502 }), 'Il servizio non risponde in questo momento. Riprova tra poco.'],
    ['400', () => new Response(null, { status: 400 }), 'La richiesta non è valida. Riprova.'],
    ['rete assente', () => Promise.reject(new TypeError('Failed to fetch')), 'Impossibile raggiungere il server. Controlla la connessione e riprova.'],
  ])('errore %s: messaggio tradotto con «Riprova» (non è «non trovata»)', async (_name, respond, message) => {
    stubActivitiesApi(respond);
    await mount();
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(message);
    expect(alert).not.toHaveTextContent('SEGRETO');
    expect(within(alert).getByRole('button', { name: 'Riprova' })).toBeInTheDocument();
    expect(screen.queryByText('Attività non trovata')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toBeInTheDocument();
  });

  it('«Riprova» ripete la richiesta e mostra il dettaglio', async () => {
    let healthy = false;
    const api = stubActivitiesApi(() => (healthy ? jsonResponse(fullDetail()) : new Response(null, { status: 503 })));
    await mount();
    const alert = await screen.findByRole('alert');
    healthy = true;
    await userEvent.setup().click(within(alert).getByRole('button', { name: 'Riprova' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Bici' })).toBeInTheDocument();
    expect(api.urls()).toHaveLength(2);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('dettaglio: «Torna all\'elenco»', () => {
  it('senza provenienza porta all\'elenco semplice', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toHaveAttribute('href', '/activities');
  });

  it('conserva il filtro e la pagina da cui si arriva', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount(`/activities/${ID}?sport=ride&page=3`);
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toHaveAttribute('href', '/activities?sport=ride&page=3');
  });

  it('ignora i parametri non validi', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount(`/activities/${ID}?sport=bogus&page=-2`);
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toHaveAttribute('href', '/activities');
  });

  it('dall\'elenco il link al dettaglio porta con sé filtro e pagina e il ritorno li ripristina', async () => {
    stubActivitiesApi((url) =>
      url.pathname === '/api/activities'
        ? jsonResponse({ items: [{ ...fullDetail(), stream: undefined }], page: 2, pageSize: 20, total: 45 })
        : jsonResponse(fullDetail()),
    );
    const { router } = await mount('/activities?sport=ride&page=2');
    const link = await within(await screen.findByRole('table', { name: 'Elenco delle attività' })).findByRole('link');
    expect(link).toHaveAttribute('href', `/activities/${ID}?sport=ride&page=2`);

    const user = userEvent.setup();
    await user.click(link);
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    await user.click(screen.getByRole('link', { name: "Torna all'elenco" }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/activities'));
    expect(router.state.location.search).toEqual({ sport: 'ride', page: 2 });
  });
});

describe('dettaglio: lingua', () => {
  it('in inglese etichette, sport e numeri seguono la lingua', async () => {
    stubActivitiesApi(() => jsonResponse(fullDetail()));
    await mount();
    await screen.findByRole('heading', { level: 1, name: 'Bici' });
    await i18n.changeLanguage('en');
    expect(await screen.findByRole('heading', { level: 1, name: 'Ride' })).toBeInTheDocument();
    expect(stat('Distance')).toHaveTextContent(`42.20 km`);
    expect(stat('Avg heart rate')).toHaveTextContent(`151 bpm`);
    expect(screen.getByRole('link', { name: 'Back to the list' })).toBeInTheDocument();
  });
});
