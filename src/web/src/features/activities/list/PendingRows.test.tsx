import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { i18n } from '@/shared/i18n';
import {
  deferred,
  externalIdOf,
  jsonResponse,
  listBody,
  makeItem,
  stubActivitiesApi,
  type FakeApi,
  type ListItem,
} from '@/test/fakeActivitiesApi';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { renderApp } from '@/test/renderApp';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { pendingUploads } from '../pending/pendingUploads';
import { PENDING_POLL_INTERVAL_MS, PENDING_TIMEOUT_MS } from './usePendingTracking';

// Righe «in elaborazione» (DEC-008, DEC-017): rilevamento esatto con `externalId`, polling ogni 2 s solo
// finché serve, timeout a 60 s. Timer finti (`shouldAdvanceTime` perché Testing Library attende col tempo reale).

const LIST_LABEL = 'Elenco delle attività';
const EXT = externalIdOf(900);
const EXT_B = externalIdOf(901);

/** L'attività con questo `externalId` esiste sul server solo se è nell'insieme `known`. */
function serverWith(known: Set<string>, visible: ListItem[] = [makeItem(1)]) {
  return stubActivitiesApi((url) => {
    const externalId = url.searchParams.get('externalId');
    if (externalId !== null) {
      const found = known.has(externalId);
      return jsonResponse(listBody(found ? [makeItem(900, { externalId })] : [], { total: found ? 1 : 0 }));
    }
    // L'elenco visibile: le attività già note e, se il rilevamento ha successo, anche l'importata.
    const imported = [...known].map((id, index) => makeItem(800 + index, { externalId: id }));
    return jsonResponse(listBody([...imported, ...visible]));
  });
}

let disposeTheme: (() => void) | undefined;

async function mount(path = '/activities') {
  const result = await renderApp(path);
  disposeTheme = result.disposeTheme;
  return result;
}

function addPending(externalId = EXT, acceptedAt = Date.now()) {
  pendingUploads.add({ externalId, uploadId: '44444444-4444-4444-4444-444444444444', acceptedAt });
}

const cards = () => screen.getByRole('list', { name: LIST_LABEL });
const table = () => screen.getByRole('table', { name: LIST_LABEL });

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

function expectPendingRows(count: number) {
  for (const container of [cards(), table()]) {
    expect(within(container).queryAllByText('In elaborazione…')).toHaveLength(count);
  }
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  resetPersistedPreferences();
  document.documentElement.classList.remove('dark');
  installFakeMatchMedia(false);
  pendingUploads.clear();
});

afterEach(async () => {
  disposeTheme?.();
  disposeTheme = undefined;
  pendingUploads.clear();
  vi.useRealTimers();
  await i18n.changeLanguage('it');
});

describe('riga «in elaborazione»', () => {
  it('compare in cima all\'elenco (schede e tabella) per un upload accettato, senza il nome del file', async () => {
    serverWith(new Set());
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(1);
    // In cima: prima delle attività vere.
    expect(within(cards()).getAllByRole('listitem')[0]).toHaveTextContent('In elaborazione…');
    expect(within(table()).getAllByRole('row')[1]).toHaveTextContent('In elaborazione…');
    // La riga non è un link (non c'è ancora un\'attività) e non mostra altro che l'istante di caricamento.
    expect(within(cards()).getAllByRole('link')).toHaveLength(1);
  });

  it('diventa un\'attività dopo il polling: riga sostituita, store svuotato, polling fermo', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(1);
    const listRequestsBefore = api.listRequests().length;
    expect(api.externalIdRequests(EXT)).toHaveLength(1); // controllo all'apertura: l'attività non c'è

    await advance(PENDING_POLL_INTERVAL_MS);
    expect(api.externalIdRequests(EXT)).toHaveLength(2);
    expectPendingRows(1);
    expect(pendingUploads.getSnapshot()).toHaveLength(1);

    // Il server importa il file: al controllo successivo l'attività esiste.
    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);

    await waitFor(() => expectPendingRows(0));
    expect(pendingUploads.getSnapshot()).toHaveLength(0);
    expect(api.listRequests().length).toBeGreaterThan(listRequestsBefore); // l'elenco è stato riscaricato
    // L'attività importata è ora nell'elenco, come riga vera con il suo link.
    expect(within(table()).getAllByRole('link')).toHaveLength(2);

    // Polling fermo: niente più richieste di rilevamento né dell'elenco.
    const externalIdCount = api.externalIdRequests().length;
    const listCount = api.listRequests().length;
    await advance(PENDING_POLL_INTERVAL_MS * 10);
    expect(api.externalIdRequests()).toHaveLength(externalIdCount);
    expect(api.listRequests()).toHaveLength(listCount);
  });

  it('trova con externalId un\'attività con data vecchia che NON è nella pagina visibile', async () => {
    const known = new Set<string>();
    // L'elenco visibile non contiene MAI l'attività vecchia (sta in una pagina successiva): lo prova l'assenza
    // di righe con il suo externalId; solo la ricerca esatta la trova.
    const api = stubActivitiesApi((url) => {
      const externalId = url.searchParams.get('externalId');
      if (externalId !== null) {
        return jsonResponse(
          listBody(known.has(externalId) ? [makeItem(900, { externalId, startTime: '2020-03-01T08:00:00+00:00' })] : []),
        );
      }
      return jsonResponse(listBody([makeItem(1), makeItem(2)], { total: 45 }));
    });
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(1);

    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expectPendingRows(0));
    expect(pendingUploads.getSnapshot()).toHaveLength(0);
    // Le richieste di rilevamento sono tutte per externalId esatto, mai una ricerca nella pagina.
    for (const url of api.externalIdRequests()) {
      expect(url.searchParams.get('externalId')).toBe(EXT);
      expect(url.searchParams.get('pageSize')).toBe('1');
    }
  });

  it('timeout a 60 s dall\'accettazione: «Ancora in elaborazione.» con «Riprova»; nessun polling dopo', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(1);
    expect(PENDING_TIMEOUT_MS).toBe(60_000);

    await advance(PENDING_TIMEOUT_MS - 1000);
    expectPendingRows(1);
    expect(screen.queryByText('Ancora in elaborazione.')).not.toBeInTheDocument();

    await advance(2000); // oltre i 60 s: ultimo controllo automatico, poi «Ancora in elaborazione.»
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
    for (const container of [cards(), table()]) {
      expect(within(container).getByText('Ancora in elaborazione.')).toBeInTheDocument();
      expect(within(container).getByRole('button', { name: 'Riprova' })).toBeEnabled();
      expect(within(container).queryByText('In elaborazione…')).not.toBeInTheDocument();
    }
    expect(pendingUploads.getSnapshot()).toHaveLength(1); // la riga resta finché non si risolve

    // Nessun polling dopo il timeout.
    const count = api.externalIdRequests().length;
    await advance(PENDING_POLL_INTERVAL_MS * 15);
    expect(api.externalIdRequests()).toHaveLength(count);
  });

  it('«Riprova» esegue un controllo singolo e, se l\'attività esiste, la risolve', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await advance(PENDING_TIMEOUT_MS + 1000);
    const retry = await within(table()).findByRole('button', { name: 'Riprova' });
    const user = userEvent.setup({ advanceTimers: (ms) => void vi.advanceTimersByTime(ms) });

    // Ancora assente: un solo controllo in più, la riga resta.
    const before = api.externalIdRequests().length;
    await user.click(retry);
    await waitFor(() => expect(api.externalIdRequests()).toHaveLength(before + 1));
    expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument();
    await advance(PENDING_POLL_INTERVAL_MS * 5);
    expect(api.externalIdRequests()).toHaveLength(before + 1);

    // Ora il file è stato importato: «Riprova» lo trova.
    known.add(EXT);
    await user.click(within(table()).getByRole('button', { name: 'Riprova' }));
    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
    expect(screen.queryByText('Ancora in elaborazione.')).not.toBeInTheDocument();
  });

  it('un upload già scaduto all\'apertura: un solo controllo, nessun polling', async () => {
    const api = serverWith(new Set());
    addPending(EXT, Date.now() - PENDING_TIMEOUT_MS * 3);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(await within(table()).findByText('Ancora in elaborazione.')).toBeInTheDocument();
    await advance(PENDING_POLL_INTERVAL_MS * 10);
    // Il controllo di montaggio è anche l'ultimo controllo automatico: UNA sola richiesta.
    expect(api.externalIdRequests(EXT)).toHaveLength(1);
  });

  it('un upload già scaduto all\'apertura ma già importato si risolve da solo', async () => {
    serverWith(new Set([EXT]));
    addPending(EXT, Date.now() - PENDING_TIMEOUT_MS * 3);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
    expect(screen.queryByText('Ancora in elaborazione.')).not.toBeInTheDocument();
  });

  it('con lo store vuoto non c\'è alcun polling né richiesta di rilevamento', async () => {
    const api = serverWith(new Set());
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await advance(PENDING_POLL_INTERVAL_MS * 10);
    expect(api.externalIdRequests()).toHaveLength(0);
    expect(api.listRequests()).toHaveLength(1);
    expectPendingRows(0);
  });

  it('un upload registrato mentre l\'elenco è aperto compare e viene seguito', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(0);

    act(() => addPending());
    await waitFor(() => expectPendingRows(1));
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1));

    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expectPendingRows(0));
    expect(pendingUploads.getSnapshot()).toHaveLength(0);
  });

  it('al massimo una richiesta per upload e per ciclo (due upload, due richieste per ciclo)', async () => {
    const api = serverWith(new Set());
    addPending(EXT);
    addPending(EXT_B);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(2);
    expect(api.externalIdRequests(EXT)).toHaveLength(1);
    expect(api.externalIdRequests(EXT_B)).toHaveLength(1);

    for (let cycle = 2; cycle <= 4; cycle += 1) {
      await advance(PENDING_POLL_INTERVAL_MS);
      expect(api.externalIdRequests(EXT)).toHaveLength(cycle);
      expect(api.externalIdRequests(EXT_B)).toHaveLength(cycle);
    }
  });

  it('un errore del rilevamento non ferma il polling: al ciclo dopo si riprova', async () => {
    let failing = true;
    const known = new Set<string>();
    const api = stubActivitiesApi((url) => {
      const externalId = url.searchParams.get('externalId');
      if (externalId === null) return jsonResponse(listBody([makeItem(1)]));
      if (failing) return new Response(null, { status: 502 });
      return jsonResponse(listBody(known.has(externalId) ? [makeItem(900, { externalId })] : []));
    });
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    expectPendingRows(1);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument(); // l'errore del rilevamento non è un errore dell'elenco

    failing = false;
    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
    expect(api.externalIdRequests(EXT).length).toBeGreaterThanOrEqual(2);
  });
});

describe('quando le righe «in elaborazione» NON si mostrano (ma il rilevamento prosegue)', () => {
  async function expectNoRowsButStillTracked(path: string, api: () => FakeApi, known: Set<string>) {
    addPending();
    await mount(path);
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(screen.queryByText('In elaborazione…')).not.toBeInTheDocument();
    expect(screen.queryByText('Ancora in elaborazione.')).not.toBeInTheDocument();
    await waitFor(() => expect(api().externalIdRequests(EXT)).toHaveLength(1));

    // Il rilevamento prosegue: appena il file è importato l'upload esce dallo store.
    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
  }

  it('con un filtro per sport', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    await expectNoRowsButStillTracked('/activities?sport=run', () => api, known);
  });

  it('a pagina 2 o successive', async () => {
    const known = new Set<string>();
    const api = stubActivitiesApi((url) => {
      const externalId = url.searchParams.get('externalId');
      if (externalId !== null) return jsonResponse(listBody(known.has(externalId) ? [makeItem(900, { externalId })] : []));
      return jsonResponse(listBody([makeItem(1)], { page: 2, total: 45 }));
    });
    await expectNoRowsButStillTracked('/activities?page=2', () => api, known);
  });

  it('tornando a pagina 1 senza filtro la riga compare', async () => {
    const api = stubActivitiesApi((url) =>
      url.searchParams.has('externalId') ? jsonResponse(listBody([])) : jsonResponse(listBody([makeItem(1)], { total: 45 })),
    );
    addPending();
    await mount('/activities?sport=run');
    await screen.findByRole('table', { name: LIST_LABEL });
    expect(screen.queryByText('In elaborazione…')).not.toBeInTheDocument();
    const user = userEvent.setup({ advanceTimers: (ms) => void vi.advanceTimersByTime(ms) });
    await user.click(screen.getByRole('button', { name: 'Tutti' }));
    await waitFor(() => expectPendingRows(1));
    expect(api.listRequests().length).toBeGreaterThan(1);
  });
});

describe('scadenza: ultimo controllo automatico', () => {
  /** Un upload accettato 59 s fa: scade 1 s dopo il montaggio, prima del secondo ciclo di polling (2 s). */
  const almostExpired = () => addPending(EXT, Date.now() - (PENDING_TIMEOUT_MS - 1000));

  /** Registra se, in un qualsiasi momento, il testo «Ancora in elaborazione.» è comparso nella pagina. */
  function watchForTimedOutText() {
    let seen = false;
    const observer = new MutationObserver(() => {
      if (document.body.textContent?.includes('Ancora in elaborazione.')) seen = true;
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    return { seen: () => seen, stop: () => observer.disconnect() };
  }

  it('un timer che scatta 1 ms in anticipo non lascia l\'upload in sospeso: la scadenza si riconosce comunque', async () => {
    const api = serverWith(new Set());
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    // Dopo il montaggio il timer è già armato: da qui `Date.now()` risulta 1 ms indietro quando scatta.
    const clock = Date.now.bind(Date);
    vi.spyOn(Date, 'now').mockImplementation(() => clock() - 1);

    await advance(PENDING_TIMEOUT_MS + 5);
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
    const count = api.externalIdRequests().length;
    await advance(PENDING_POLL_INTERVAL_MS * 10);
    expect(api.externalIdRequests()).toHaveLength(count); // polling fermo
  });

  it('scaduto con l\'attività presente: l\'ultimo controllo la trova e la risolve, senza mai mostrare «Riprova»', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    almostExpired();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1)); // controllo di montaggio: ancora assente
    expectPendingRows(1);

    const watcher = watchForTimedOutText();
    known.add(EXT); // importata prima della scadenza ma dopo l'ultimo controllo
    await advance(1100); // scadenza (a +1 s), prima del secondo ciclo di polling (a +2 s)

    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
    await waitFor(() => expectPendingRows(0));
    watcher.stop();
    expect(watcher.seen()).toBe(false);
    expect(screen.queryByRole('button', { name: 'Riprova' })).not.toBeInTheDocument();
    expect(api.externalIdRequests(EXT)).toHaveLength(2); // montaggio + UN ultimo controllo
  });

  it('scaduto con l\'attività assente: UN controllo automatico, poi «Ancora in elaborazione.» con «Riprova»', async () => {
    const api = serverWith(new Set());
    almostExpired();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1));

    await advance(1100);
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
    expect(within(table()).getByRole('button', { name: 'Riprova' })).toBeEnabled();
    expect(api.externalIdRequests(EXT)).toHaveLength(2);

    await advance(PENDING_POLL_INTERVAL_MS * 15);
    expect(api.externalIdRequests(EXT)).toHaveLength(2); // nessuna tempesta, nessun altro controllo automatico
  });

  it('durante l\'ultimo controllo la riga resta «in elaborazione» (il «Riprova» non compare prima dell\'esito)', async () => {
    const gate = deferred<Response>();
    let holdDetection = false;
    const api = stubActivitiesApi((url) => {
      if (!url.searchParams.has('externalId')) return jsonResponse(listBody([makeItem(1)]));
      return holdDetection ? gate.promise : jsonResponse(listBody([]));
    });
    almostExpired();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1));

    holdDetection = true;
    await advance(1100);
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(2));
    expectPendingRows(1);
    expect(screen.queryByRole('button', { name: 'Riprova' })).not.toBeInTheDocument();

    gate.resolve(jsonResponse(listBody([])));
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
  });

  it('l\'ultimo controllo è uno solo per upload: un altro upload registrato dopo non lo ripete', async () => {
    const api = serverWith(new Set());
    almostExpired();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await advance(1100);
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
    expect(api.externalIdRequests(EXT)).toHaveLength(2);

    act(() => addPending(EXT_B)); // cambia `pending`: l'effetto dell'ultimo controllo si rivaluta
    await waitFor(() => expect(api.externalIdRequests(EXT_B)).toHaveLength(1));
    await advance(PENDING_POLL_INTERVAL_MS);
    expect(api.externalIdRequests(EXT)).toHaveLength(2);
  });

  function setVisibility(state: 'visible' | 'hidden') {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  afterEach(() => {
    // Ripristina la proprietà di prototipo (era stata nascosta da `defineProperty` sull'istanza).
    Reflect.deleteProperty(document, 'visibilityState');
  });

  it('tornando visibile dopo la scadenza (timer sospesi) si esegue l\'ultimo controllo', async () => {
    const api = serverWith(new Set());
    addPending(EXT, Date.now() - 30_000);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1));
    expectPendingRows(1);

    // La scheda va in secondo piano e il tempo passa (oltre la scadenza) senza che i timer scattino.
    setVisibility('hidden');
    vi.setSystemTime(Date.now() + 120_000);
    expectPendingRows(1);
    expect(api.externalIdRequests(EXT)).toHaveLength(1);

    setVisibility('visible');
    await waitFor(() => expect(within(table()).getByText('Ancora in elaborazione.')).toBeInTheDocument());
    expect(api.externalIdRequests(EXT)).toHaveLength(2); // UN controllo al ritorno
    await advance(PENDING_POLL_INTERVAL_MS * 5);
    expect(api.externalIdRequests(EXT)).toHaveLength(2);
  });

  it('tornando visibile dopo la scadenza con l\'attività ormai presente la risolve', async () => {
    const known = new Set<string>();
    const api = serverWith(known);
    addPending(EXT, Date.now() - 30_000);
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    await waitFor(() => expect(api.externalIdRequests(EXT)).toHaveLength(1));

    setVisibility('hidden');
    vi.setSystemTime(Date.now() + 120_000);
    known.add(EXT);
    setVisibility('visible');
    await waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(0));
    expect(screen.queryByText('Ancora in elaborazione.')).not.toBeInTheDocument();
  });
});

describe('aggiornamento dell\'elenco e annunci', () => {
  const announcer = () => screen.getByTestId('pending-announcer');

  it('la riga «in elaborazione» resta finché l\'elenco aggiornato non è arrivato (nessun vuoto tra riga e attività)', async () => {
    const known = new Set<string>();
    let holdList: ReturnType<typeof deferred<Response>> | null = null;
    const api = stubActivitiesApi((url) => {
      const externalId = url.searchParams.get('externalId');
      if (externalId !== null) return jsonResponse(listBody(known.has(externalId) ? [makeItem(900, { externalId })] : []));
      const items = [...known].map((id) => makeItem(900, { externalId: id }));
      const response = jsonResponse(listBody([...items, makeItem(1)]));
      return holdList ? holdList.promise.then(() => response) : response;
    });
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });

    // Il server importa il file; la risposta dell'elenco aggiornato resta «in volo».
    holdList = deferred<Response>();
    known.add(EXT);
    const listBefore = api.listRequests().length;
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expect(api.listRequests().length).toBe(listBefore + 1)); // l'elenco è stato richiesto
    expectPendingRows(1); // la riga c'è ancora
    expect(pendingUploads.getSnapshot()).toHaveLength(1);
    expect(within(table()).getAllByRole('link')).toHaveLength(1);

    holdList.resolve(new Response());
    await waitFor(() => expectPendingRows(0));
    expect(pendingUploads.getSnapshot()).toHaveLength(0);
    expect(within(table()).getAllByRole('link')).toHaveLength(2); // ora c'è l'attività vera
  });

  it('la regione live c\'è fin dal primo disegno (anche durante il caricamento) ed è vuota', async () => {
    const gate = deferred<Response>();
    stubActivitiesApi(() => gate.promise);
    await mount();
    await screen.findByRole('status'); // caricamento in corso
    expect(announcer()).toHaveAttribute('aria-live', 'polite');
    expect(announcer()).toHaveClass('sr-only');
    expect(announcer()).toBeEmptyDOMElement();
  });

  it('annuncia «Attività importata.» quando l\'upload si risolve', async () => {
    const known = new Set<string>();
    serverWith(known);
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const region = announcer();
    expect(region).toBeEmptyDOMElement();

    known.add(EXT);
    await advance(PENDING_POLL_INTERVAL_MS);
    await waitFor(() => expect(announcer()).toHaveTextContent('Attività importata.'));
    expect(announcer()).toBe(region); // è la stessa regione, non una nuova inserita dopo
  });

  it('annuncia che un caricamento è ancora in elaborazione quando scade il timeout', async () => {
    serverWith(new Set());
    addPending();
    await mount();
    await screen.findByRole('table', { name: LIST_LABEL });
    const region = announcer();
    await advance(PENDING_TIMEOUT_MS + 1000);
    await waitFor(() => expect(announcer()).toHaveTextContent('è ancora in elaborazione'));
    expect(announcer()).toBe(region);
  });
});
