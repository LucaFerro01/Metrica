import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { i18n } from '@/shared/i18n';
import { formatDateTime } from '@/shared/lib/format';
import {
  deferred,
  jsonResponse,
  listBody,
  makeItem,
  activityId,
  stubActivitiesApi,
  type ListItem,
} from '@/test/fakeActivitiesApi';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { renderApp } from '@/test/renderApp';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { pendingUploads } from '../pending/pendingUploads';

const LIST_LABEL = 'Elenco delle attività';
// getByText normalizza gli spazi (anche quello non separabile tra numero e unità) nel testo del nodo.
const NBSP = ' ';

let disposeTheme: (() => void) | undefined;

async function mount(path = '/activities') {
  const result = await renderApp(path);
  disposeTheme = result.disposeTheme;
  return result;
}

const cards = () => screen.getByRole('list', { name: LIST_LABEL });
const table = () => screen.getByRole('table', { name: LIST_LABEL });

beforeEach(() => {
  resetPersistedPreferences();
  document.documentElement.classList.remove('dark');
  installFakeMatchMedia(false);
  pendingUploads.clear();
});

afterEach(async () => {
  disposeTheme?.();
  disposeTheme = undefined;
  pendingUploads.clear();
  await i18n.changeLanguage('it');
});

const twoItems = (): ListItem[] => [
  makeItem(1),
  makeItem(2, {
    sport: 'ride',
    startTime: '2026-11-18T18:05:00+00:00',
    distanceMeters: 850,
    elapsedSeconds: 59,
    elevationGainMeters: null,
  }),
];

describe('elenco: contenuto delle righe', () => {
  it('mostra data e ora, sport, distanza, durata e dislivello, e «non disponibile» per il dislivello nullo', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    await mount();

    for (const container of [() => cards(), () => table()]) {
      const view = within(await waitFor(() => container()));
      expect(view.getByText(formatDateTime('2026-11-19T07:30:00+00:00', 'it') ?? 'x')).toBeInTheDocument();
      expect(view.getByText('Corsa')).toBeInTheDocument();
      expect(view.getByText(`12,35${NBSP}km`)).toBeInTheDocument();
      expect(view.getByText('1:02:05')).toBeInTheDocument();
      expect(view.getByText(`123${NBSP}m`)).toBeInTheDocument();

      expect(view.getByText('Bici')).toBeInTheDocument();
      expect(view.getByText(`850${NBSP}m`)).toBeInTheDocument();
      expect(view.getByText('0:59')).toBeInTheDocument();
      // Il dislivello assente non è «0» né vuoto.
      expect(view.getAllByText('non disponibile')).toHaveLength(1);
      expect(view.queryByText(`0${NBSP}m`)).not.toBeInTheDocument();
    }
  });

  it('mantiene l\'ordine del server', async () => {
    const items = [makeItem(3), makeItem(1), makeItem(2)];
    stubActivitiesApi(() => jsonResponse(listBody(items)));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const hrefs = within(table())
      .getAllByRole('link')
      .map((link) => link.getAttribute('href'));
    expect(hrefs).toEqual(items.map((item) => `/activities/${item.id}`));
  });

  it('chiede la prima pagina da 20 elementi, senza filtro per sport', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)])));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const [url] = api.listRequests();
    expect(url?.pathname).toBe('/api/activities');
    expect(url?.searchParams.get('page')).toBe('1');
    expect(url?.searchParams.get('pageSize')).toBe('20');
    expect(url?.searchParams.has('sport')).toBe(false);
    expect(url?.searchParams.has('externalId')).toBe(false);
  });
});

describe('elenco: schede sotto 768 px e tabella da 768 px (le media query non si valutano in jsdom: si verificano le classi)', () => {
  it('ci sono entrambe le viste con lo stesso contenuto, alternate da md:hidden e hidden md:block', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });

    expect(cards()).toHaveClass('md:hidden');
    expect(cards()).not.toHaveClass('hidden');
    const tableWrapper = table().parentElement;
    expect(tableWrapper).toHaveClass('hidden', 'md:block');
    expect(tableWrapper).not.toHaveClass('md:hidden');

    expect(within(cards()).getAllByRole('listitem')).toHaveLength(2);
    expect(within(table()).getAllByRole('row')).toHaveLength(3); // intestazione + 2 attività
  });

  it('ogni riga è un link al dettaglio, in entrambe le viste', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const expected = [`/activities/${activityId(1)}`, `/activities/${activityId(2)}`];
    expect(within(cards()).getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual(expected);
    expect(within(table()).getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual(expected);
  });

  it('le intestazioni della tabella sono tradotte e associate alle colonne', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const headers = within(table()).getAllByRole('columnheader').map((header) => header.textContent);
    expect(headers).toEqual(['Data e ora', 'Sport', 'Distanza', 'Durata', 'Dislivello']);
  });

  it('cliccando la riga (non solo il link) si apre il dettaglio', async () => {
    stubActivitiesApi((url) =>
      url.pathname === '/api/activities' ? jsonResponse(listBody(twoItems())) : jsonResponse({}, 404),
    );
    const { router } = await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const user = userEvent.setup();
    await user.click(within(table()).getByText('Bici')); // una cella qualsiasi della seconda riga
    await waitFor(() => expect(router.state.location.pathname).toBe(`/activities/${activityId(2)}`));
  });

  it('con del testo selezionato (per copiarlo) il clic sulla riga non apre il dettaglio', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    const { router } = await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const cell = within(table()).getByText('Bici');

    window.getSelection()?.selectAllChildren(cell); // selezione attiva
    fireEvent.click(cell);
    await new Promise((resolve) => setTimeout(resolve, 50)); // il clic non deve produrre alcuna navigazione
    expect(router.state.location.pathname).toBe('/activities');

    window.getSelection()?.removeAllRanges(); // senza selezione il clic apre il dettaglio
    fireEvent.click(cell);
    await waitFor(() => expect(router.state.location.pathname).toBe(`/activities/${activityId(2)}`));
  });
});

describe('elenco: filtro per sport', () => {
  it('ha i cinque pulsanti da 44 px e «Tutti» è selezionato di partenza', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    await mount();
    const group = await screen.findByRole('group', { name: 'Filtra per sport' });
    const buttons = within(group).getAllByRole('button');
    expect(buttons.map((button) => button.getAttribute('aria-label'))).toEqual([
      'Tutti',
      'Corsa',
      'Bici',
      'Nuoto',
      'Altro',
    ]);
    for (const button of buttons) expect(button).toHaveClass('min-h-11', 'min-w-11');
    expect(within(group).getByRole('button', { name: 'Tutti' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('scegliere uno sport cambia la richiesta e l\'indirizzo; «Tutti» li ripristina', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    const { router } = await mount();
    const group = await screen.findByRole('group', { name: 'Filtra per sport' });
    await screen.findByRole('table', { name: LIST_LABEL });
    const user = userEvent.setup();

    await user.click(within(group).getByRole('button', { name: 'Bici' }));
    await waitFor(() => expect(api.listRequests().at(-1)?.searchParams.get('sport')).toBe('ride'));
    expect(api.listRequests().at(-1)?.searchParams.get('page')).toBe('1');
    expect(router.state.location.search).toEqual({ sport: 'ride' });
    expect(within(group).getByRole('button', { name: 'Bici' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'Tutti' })).toHaveAttribute('aria-pressed', 'false');

    await user.click(within(group).getByRole('button', { name: 'Tutti' }));
    await waitFor(() => expect(api.listRequests().at(-1)?.searchParams.has('sport')).toBe(false));
    expect(router.state.location.search).toEqual({});
  });

  it('cambiare sport riparte dalla prima pagina', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)], { page: 3, total: 60 })));
    const { router } = await mount('/activities?sport=ride&page=3');
    const group = await screen.findByRole('group', { name: 'Filtra per sport' });
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(api.listRequests()[0]?.searchParams.get('page')).toBe('3');

    await userEvent.setup().click(within(group).getByRole('button', { name: 'Nuoto' }));
    await waitFor(() => expect(api.listRequests().at(-1)?.searchParams.get('sport')).toBe('swim'));
    expect(api.listRequests().at(-1)?.searchParams.get('page')).toBe('1');
    expect(router.state.location.search).toEqual({ sport: 'swim' });
  });

  it('un indirizzo con ?sport= è già filtrato', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)])));
    await mount('/activities?sport=swim');
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(api.listRequests()[0]?.searchParams.get('sport')).toBe('swim');
    expect(screen.getByRole('button', { name: 'Nuoto' })).toHaveAttribute('aria-pressed', 'true');
  });
});

describe('elenco: paginazione', () => {
  const pageOf = (url: URL) => Number(url.searchParams.get('page') ?? '1');

  it('prima pagina: «Precedente» disabilitato, «Pagina 1 di 3», «Successiva» attiva; poi la seconda e l\'ultima', async () => {
    const api = stubActivitiesApi((url) =>
      jsonResponse(listBody([makeItem(pageOf(url))], { page: pageOf(url), total: 45 })),
    );
    const { router } = await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const nav = screen.getByRole('navigation', { name: 'Paginazione' });
    const previous = within(nav).getByRole('button', { name: 'Precedente' });
    const next = within(nav).getByRole('button', { name: 'Successiva' });
    expect(within(nav).getByText('Pagina 1 di 3')).toBeInTheDocument();
    expect(previous).toBeDisabled();
    expect(next).toBeEnabled();

    const user = userEvent.setup();
    await user.click(next);
    await waitFor(() => expect(within(nav).getByText('Pagina 2 di 3')).toBeInTheDocument());
    expect(api.listRequests().at(-1)?.searchParams.get('page')).toBe('2');
    expect(router.state.location.search).toEqual({ page: 2 });
    expect(previous).toBeEnabled();
    expect(next).toBeEnabled();

    await user.click(next);
    await waitFor(() => expect(within(nav).getByText('Pagina 3 di 3')).toBeInTheDocument());
    expect(next).toBeDisabled();
    expect(previous).toBeEnabled();

    await user.click(previous);
    await user.click(previous);
    await waitFor(() => expect(within(nav).getByText('Pagina 1 di 3')).toBeInTheDocument());
    expect(router.state.location.search).toEqual({});
    expect(previous).toBeDisabled();
  });

  it('con una sola pagina entrambi i pulsanti sono disabilitati', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems(), { total: 20 })));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const nav = screen.getByRole('navigation', { name: 'Paginazione' });
    expect(within(nav).getByText('Pagina 1 di 1')).toBeInTheDocument();
    expect(within(nav).getByRole('button', { name: 'Precedente' })).toBeDisabled();
    expect(within(nav).getByRole('button', { name: 'Successiva' })).toBeDisabled();
  });

  it('21 attività sono 2 pagine (il confine è sopra i 20)', async () => {
    stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)], { total: 21 })));
    await mount();
    await screen.findByText('Pagina 1 di 2');
  });

  it('un indirizzo con ?page=2 chiede la seconda pagina e la riflette', async () => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)], { page: 2, total: 45 })));
    await mount('/activities?page=2');
    await screen.findByText('Pagina 2 di 3');
    expect(api.listRequests()[0]?.searchParams.get('page')).toBe('2');
  });

  it('una pagina oltre l\'ultima spiega che non esiste e porta all\'ultima', async () => {
    stubActivitiesApi((url) =>
      jsonResponse(listBody(pageOf(url) > 2 ? [] : [makeItem(1)], { page: pageOf(url), total: 25 })),
    );
    await mount('/activities?page=9');
    expect(await screen.findByText('Questa pagina non esiste.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Vai all'ultima pagina" })).toHaveAttribute('href', '/activities?page=2');
  });
});

describe('elenco: parametri di ricerca non validi non sono errori', () => {
  it.each([
    ['sport sconosciuto', '?sport=triathlon'],
    ['sport con maiuscole', '?sport=Run'],
    ['sport vuoto', '?sport='],
    ['pagina zero', '?page=0'],
    ['pagina negativa', '?page=-3'],
    ['pagina non numerica', '?page=abc'],
    ['pagina decimale', '?page=2.5'],
    ['pagina enorme', '?page=99999999999'],
    ['pagina uno', '?page=1'],
  ])('%s: valgono i predefiniti (tutti gli sport, pagina 1)', async (_name, search) => {
    const api = stubActivitiesApi(() => jsonResponse(listBody([makeItem(1)])));
    await mount(`/activities${search}`);
    await screen.findByRole('table', { name: LIST_LABEL });
    const [url] = api.listRequests();
    expect(url?.searchParams.has('sport')).toBe(false);
    expect(url?.searchParams.get('page')).toBe('1');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('elenco: caricamento, vuoto ed errori', () => {
  it('mostra lo stato di caricamento finché la risposta non arriva', async () => {
    const gate = deferred<Response>();
    stubActivitiesApi(() => gate.promise);
    await mount();
    expect(await screen.findByRole('status')).toHaveTextContent('Caricamento delle attività…');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    gate.resolve(jsonResponse(listBody([makeItem(1)])));
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(screen.queryByText('Caricamento delle attività…')).not.toBeInTheDocument();
  });

  it('senza attività: messaggio e link a Carica', async () => {
    stubActivitiesApi(() => jsonResponse(listBody([])));
    await mount();
    expect(await screen.findByText('Nessuna attività: carica un file per iniziare.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Carica un file' })).toHaveAttribute('href', '/upload');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: 'Paginazione' })).not.toBeInTheDocument();
  });

  it('con un filtro che non trova nulla il messaggio è per lo sport, senza invito a caricare', async () => {
    stubActivitiesApi(() => jsonResponse(listBody([])));
    await mount('/activities?sport=swim');
    expect(await screen.findByText('Nessuna attività per questo sport.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Carica un file' })).not.toBeInTheDocument();
  });

  it.each([
    ['500', () => jsonResponse({ title: 'SEGRETO-DEL-SERVER' }, 500), 'Il servizio non risponde in questo momento. Riprova tra poco.'],
    ['502 a corpo vuoto (gateway)', () => new Response(null, { status: 502 }), 'Il servizio non risponde in questo momento. Riprova tra poco.'],
    ['504 a corpo vuoto (gateway)', () => new Response(null, { status: 504 }), 'Il servizio non risponde in questo momento. Riprova tra poco.'],
    ['400', () => jsonResponse({ detail: 'SEGRETO-DEL-SERVER' }, 400), 'La richiesta non è valida. Riprova.'],
    ['400 a corpo vuoto (Kestrel)', () => new Response(null, { status: 400 }), 'La richiesta non è valida. Riprova.'],
    ['200 con testo che non è JSON', () => new Response('<html>oops</html>', { status: 200 }), 'Impossibile raggiungere il server. Controlla la connessione e riprova.'],
    ['200 con corpo vuoto', () => new Response(null, { status: 200 }), 'Risposta inattesa dal server. Riprova.'],
    ['200 con JSON di forma sbagliata', () => jsonResponse({ items: 'no' }), 'Risposta inattesa dal server. Riprova.'],
    ['rete assente', () => Promise.reject(new TypeError('Failed to fetch')), 'Impossibile raggiungere il server. Controlla la connessione e riprova.'],
  ])('errore %s: messaggio tradotto con «Riprova», mai il testo del server', async (_name, respond, message) => {
    stubActivitiesApi(respond);
    await mount();
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(message);
    expect(alert).not.toHaveTextContent('SEGRETO');
    expect(within(alert).getByRole('button', { name: 'Riprova' })).toBeEnabled();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('«Riprova» ripete la richiesta e, se riesce, mostra l\'elenco', async () => {
    let healthy = false;
    const api = stubActivitiesApi(() => (healthy ? jsonResponse(listBody(twoItems())) : new Response(null, { status: 502 })));
    await mount();
    const alert = await screen.findByRole('alert');
    expect(api.listRequests()).toHaveLength(1); // nessun tentativo automatico

    healthy = true;
    await userEvent.setup().click(within(alert).getByRole('button', { name: 'Riprova' }));
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(api.listRequests()).toHaveLength(2);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('elenco: lingua e tema', () => {
  it('in inglese i testi, le etichette degli sport e i numeri seguono la lingua', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems(), { total: 45 })));
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await i18n.changeLanguage('en');
    const englishTable = await screen.findByRole('table', { name: 'Activity list' });
    expect(within(englishTable).getByText('Run')).toBeInTheDocument();
    expect(within(englishTable).getByText('Ride')).toBeInTheDocument();
    expect(within(englishTable).getByText(`12.35${NBSP}km`)).toBeInTheDocument();
    expect(within(englishTable).getByText('not available')).toBeInTheDocument();
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
  });

  it('non usa colori fissi: solo le classi semantiche del tema', async () => {
    stubActivitiesApi(() => jsonResponse(listBody(twoItems())));
    const { container } = await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const html = container.innerHTML;
    expect(html).not.toMatch(/\b(?:bg|text|border)-(?:white|black|gray|zinc|slate|red|blue|green)\b/);
    expect(html).not.toMatch(/style="[^"]*#[0-9a-f]{3,6}/i);
  });
});
