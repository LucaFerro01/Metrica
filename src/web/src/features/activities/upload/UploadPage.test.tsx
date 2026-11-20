import { act, cleanup, fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { i18n } from '@/shared/i18n';
import { FakeXhr, installFakeXhr } from '@/test/fakeXhr';
import { renderApp } from '@/test/renderApp';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { pendingUploads } from '../pending/pendingUploads';

// Spazio non separabile tra numero e unità (come `formatFileSize`).
const NBSP = ' ';
const MIB = 1024 * 1024;
const EXTERNAL_ID = 'd'.repeat(64);
const UPLOAD_ID = '44444444-4444-4444-4444-444444444444';
const ACCEPTED = { uploadId: UPLOAD_ID, externalId: EXTERNAL_ID, status: 'accepted' };

/** Un file con la dimensione dichiarata (senza allocare i byte): serve per provare le soglie. */
function fileOfSize(name: string, size: number): File {
  const file = new File(['x'], name);
  Object.defineProperty(file, 'size', { value: size });
  return file;
}

function gpx(name = 'corsa.gpx'): File {
  return new File(['<gpx>dati</gpx>'], name, { type: 'application/gpx+xml' });
}

let user: ReturnType<typeof userEvent.setup>;
let disposeTheme: (() => void) | undefined;

async function mount() {
  const result = await renderApp('/upload');
  disposeTheme = result.disposeTheme;
  await screen.findByRole('heading', { level: 1, name: 'Carica un file' });
  return result;
}

function input(): HTMLInputElement {
  return screen.getByLabelText('Scegli un file') as HTMLInputElement;
}

async function choose(file: File) {
  await user.upload(input(), file);
}

async function chooseAndSend(file: File = gpx()) {
  await choose(file);
  await user.click(screen.getByRole('button', { name: 'Invia file' }));
}

function alertText(): string {
  return (screen.getByRole('alert').textContent ?? '').trim();
}

function statusText(): string {
  return (screen.getByRole('status').textContent ?? '').trim();
}

beforeEach(() => {
  // `applyAccept: false`: in un browser vero l'utente può scegliere "Tutti i file" e consegnare un .txt.
  user = userEvent.setup({ applyAccept: false });
  resetPersistedPreferences();
  document.documentElement.classList.remove('dark');
  installFakeMatchMedia(false);
  installFakeXhr();
  pendingUploads.clear();
});

afterEach(async () => {
  cleanup();
  disposeTheme?.();
  disposeTheme = undefined;
  pendingUploads.clear();
  await i18n.changeLanguage('it');
});

describe('pagina Carica: aspetto iniziale', () => {
  it('ha un input file etichettato che accetta .gpx e .fit, per un solo file', async () => {
    await mount();
    const field = input();
    expect(field).toHaveAttribute('type', 'file');
    expect(field).toHaveAttribute('accept', '.gpx,.fit');
    expect(field).not.toHaveAttribute('multiple');
    expect(screen.getByText('Nessun file scelto')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Annulla' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('ha un\'area di stato sempre presente (aria-live) e l\'area di trascinamento', async () => {
    await mount();
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite');
    expect(screen.getByRole('group', { name: 'Area di selezione del file' })).toBeInTheDocument();
    expect(screen.getByText(/trascina/i)).toBeInTheDocument();
  });

  it('i controlli hanno un bersaglio di tocco di almeno 44 px', async () => {
    await mount();
    expect(screen.getByRole('button', { name: 'Invia file' })).toHaveClass('min-h-11');
    expect(screen.getByText('Scegli un file', { selector: 'label' })).toHaveClass('min-h-11');
  });
});

describe('pagina Carica: scelta e invio di un file valido', () => {
  it('mostra nome e dimensione del file scelto e abilita Carica', async () => {
    await mount();
    await choose(new File([new Uint8Array(2048)], 'corsa.gpx'));
    expect(screen.getByText('corsa.gpx')).toBeInTheDocument();
    expect(screen.getByText('2 KiB')).toBeInTheDocument(); // getByText normalizza lo spazio non separabile
    expect(screen.queryByText('Nessun file scelto')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeEnabled();
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('un file valido è inviato e, con 202, dà l\'esito "caricato, in elaborazione" e registra l\'upload in sospeso', async () => {
    await mount();
    const before = Date.now();
    await chooseAndSend(gpx());

    const xhr = FakeXhr.last();
    expect(xhr.method).toBe('POST');
    expect(xhr.url).toBe('/api/ingestion/uploads');
    expect((xhr.sentFile() as File).name).toBe('corsa.gpx');

    act(() => xhr.respond(202, ACCEPTED));
    expect(await screen.findByText('File caricato: è in elaborazione.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();

    const pending = pendingUploads.getSnapshot();
    expect(pending).toHaveLength(1);
    expect(pending[0]).toMatchObject({ externalId: EXTERNAL_ID, uploadId: UPLOAD_ID });
    expect(pending[0]?.acceptedAt).toBeGreaterThanOrEqual(before);
    expect(pending[0]?.acceptedAt).toBeLessThanOrEqual(Date.now());
    // Né il nome del file né il contenuto finiscono nello store.
    expect(JSON.stringify(pending)).not.toContain('corsa');

    expect(screen.getByRole('link', { name: 'Vai alle attività' })).toHaveAttribute('href', '/activities');
  });

  it('dopo l\'esito il file non è più selezionato (niente doppio invio) e il nome non compare nell\'esito', async () => {
    await mount();
    await chooseAndSend(gpx('segreto-privato.gpx'));
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');
    expect(statusText()).not.toContain('segreto-privato');
    expect(screen.queryByText('segreto-privato.gpx')).not.toBeInTheDocument();
    expect(screen.getByText('Nessun file scelto')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
  });

  it('con "duplicate" (200) mostra l\'avviso e lascia lo store invariato', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(200, { ...ACCEPTED, status: 'duplicate' }));
    expect(await screen.findByText('Questo file era già stato caricato.')).toBeInTheDocument();
    expect(pendingUploads.getSnapshot()).toEqual([]);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Vai alle attività' })).toHaveAttribute('href', '/activities');
  });

  it('prima dell\'esito non c\'è il link all\'elenco', async () => {
    await mount();
    await chooseAndSend();
    expect(screen.queryByRole('link', { name: 'Vai alle attività' })).not.toBeInTheDocument();
  });
});

describe('pagina Carica: avanzamento e annullamento', () => {
  it('mostra la barra di avanzamento durante l\'invio, con aria-valuenow crescente', async () => {
    await mount();
    await chooseAndSend();
    const xhr = FakeXhr.last();

    const bar = screen.getByRole('progressbar', { name: 'Avanzamento del caricamento' });
    expect(bar).toHaveAttribute('aria-valuemin', '0');
    expect(bar).toHaveAttribute('aria-valuemax', '100');
    expect(bar).toHaveAttribute('aria-valuenow', '0');

    const seen: number[] = [];
    for (const [loaded, expected] of [
      [25, '25'],
      [60, '60'],
      [100, '100'],
    ] as const) {
      act(() => xhr.emitProgress(loaded, 100));
      expect(bar).toHaveAttribute('aria-valuenow', expected);
      seen.push(Number(bar.getAttribute('aria-valuenow')));
      if (loaded < 100) expect(screen.getByText(`${loaded} %`)).toBeInTheDocument();
    }
    expect(seen).toEqual([...seen].sort((a, b) => a - b));
    expect(screen.getByText('Elaborazione sul server…')).toBeInTheDocument();
    expect(screen.queryByText('100 %')).not.toBeInTheDocument();
  });

  it('durante l\'invio Carica e la scelta del file sono disattivati e c\'è Annulla', async () => {
    await mount();
    await chooseAndSend();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
    expect(input()).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Annulla' })).toBeEnabled();
  });

  it('Annulla interrompe l\'invio senza esito: nessun errore, nessun successo, nulla nello store', async () => {
    await mount();
    await chooseAndSend();
    const xhr = FakeXhr.last();
    act(() => xhr.emitProgress(30, 100));

    await user.click(screen.getByRole('button', { name: 'Annulla' }));
    expect(xhr.aborted).toBe(true);
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByText('File caricato: è in elaborazione.')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Vai alle attività' })).not.toBeInTheDocument();
    expect(pendingUploads.getSnapshot()).toEqual([]);
    // Il file resta scelto e si può riprovare.
    expect(screen.getByText('corsa.gpx')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Annulla' })).not.toBeInTheDocument();
  });

  it('una risposta arrivata dopo Annulla non produce nessun esito', async () => {
    await mount();
    await chooseAndSend();
    const xhr = FakeXhr.last();
    await user.click(screen.getByRole('button', { name: 'Annulla' }));
    act(() => xhr.respond(202, ACCEPTED));
    expect(screen.queryByText('File caricato: è in elaborazione.')).not.toBeInTheDocument();
    expect(pendingUploads.getSnapshot()).toEqual([]);
  });

  it('dopo Annulla si può inviare di nuovo lo stesso file', async () => {
    await mount();
    await chooseAndSend();
    await user.click(screen.getByRole('button', { name: 'Annulla' }));
    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    expect(FakeXhr.instances).toHaveLength(2);
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    expect(await screen.findByText('File caricato: è in elaborazione.')).toBeInTheDocument();
  });

  it('lasciare la pagina durante l\'invio NON interrompe la richiesta: con 202 l\'upload si registra comunque', async () => {
    const errors = ['error', 'warn'].map((m) =>
      vi.spyOn(console, m as 'error' | 'warn').mockImplementation(() => undefined),
    );
    const { unmount } = await mount();
    await chooseAndSend();
    const xhr = FakeXhr.last();
    unmount();
    expect(xhr.aborted).toBe(false);

    act(() => xhr.emitProgress(50, 100)); // nessun aggiornamento di stato su un componente smontato
    act(() => xhr.respond(202, ACCEPTED));
    await vi.waitFor(() => expect(pendingUploads.getSnapshot()).toHaveLength(1));
    expect(pendingUploads.getSnapshot()[0]).toMatchObject({ externalId: EXTERNAL_ID, uploadId: UPLOAD_ID });
    for (const spy of errors) expect(spy).not.toHaveBeenCalled();
  });

  it('lasciare la pagina e ricevere un errore non registra nulla né produce errori', async () => {
    const errors = ['error', 'warn'].map((m) =>
      vi.spyOn(console, m as 'error' | 'warn').mockImplementation(() => undefined),
    );
    const { unmount } = await mount();
    await chooseAndSend();
    const xhr = FakeXhr.last();
    unmount();
    act(() => xhr.respond(502, ''));
    await Promise.resolve();
    expect(pendingUploads.getSnapshot()).toEqual([]);
    for (const spy of errors) expect(spy).not.toHaveBeenCalled();
  });

  it('acceptedAt è l\'istante della risposta 202, non quello dell\'avvio dell\'invio', async () => {
    const now = vi.spyOn(Date, 'now').mockReturnValue(1_000);
    await mount();
    await chooseAndSend();
    now.mockReturnValue(5_000);
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');
    expect(pendingUploads.getSnapshot()[0]?.acceptedAt).toBe(5_000);
  });

  it('senza lunghezza calcolabile la barra è indeterminata (niente aria-valuenow) con «Invio in corso…»', async () => {
    await mount();
    await chooseAndSend();
    const bar = screen.getByRole('progressbar', { name: 'Avanzamento del caricamento' });
    expect(bar).toHaveAttribute('aria-valuenow', '0');

    act(() => FakeXhr.last().emitIndeterminateProgress(1234));
    expect(bar).not.toHaveAttribute('aria-valuenow');
    expect(screen.getByText('Invio in corso…')).toBeInTheDocument();

    // Se poi la lunghezza diventa nota si torna alla barra determinata.
    act(() => FakeXhr.last().emitProgress(40, 100));
    expect(bar).toHaveAttribute('aria-valuenow', '40');
    expect(screen.queryByText('Invio in corso…')).not.toBeInTheDocument();
  });

  it('a 100% mostra «Elaborazione sul server…» finché non arriva l\'esito', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().emitProgress(99, 100));
    expect(screen.queryByText('Elaborazione sul server…')).not.toBeInTheDocument();
    act(() => FakeXhr.last().emitProgress(100, 100));
    expect(screen.getByText('Elaborazione sul server…')).toBeInTheDocument();

    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');
    expect(screen.queryByText('Elaborazione sul server…')).not.toBeInTheDocument();
  });

  it('un doppio clic sincrono su «Invia file» fa partire una sola richiesta', async () => {
    await mount();
    await choose(gpx());
    const button = screen.getByRole('button', { name: 'Invia file' });
    act(() => {
      button.click();
      button.click();
    });
    expect(FakeXhr.instances).toHaveLength(1);
  });
});

describe('pagina Carica: focus da tastiera', () => {
  it('all\'avvio dell\'invio il focus passa a «Annulla»', async () => {
    await mount();
    await chooseAndSend();
    expect(screen.getByRole('button', { name: 'Annulla' })).toHaveFocus();
  });

  it('a esito positivo il focus passa al messaggio di esito', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');
    expect(document.activeElement).toHaveTextContent('File caricato: è in elaborazione.');
    expect(document.activeElement).toHaveAttribute('tabindex', '-1');
    expect(screen.getByRole('status')).toContainElement(document.activeElement as HTMLElement);
  });

  it('per un duplicato il focus passa al messaggio di esito', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(200, { ...ACCEPTED, status: 'duplicate' }));
    await screen.findByText('Questo file era già stato caricato.');
    expect(document.activeElement).toHaveTextContent('Questo file era già stato caricato.');
  });

  it('dopo un errore il focus resta sul messaggio (non si perde sul body)', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(502, ''));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveFocus();
    expect(alert).toHaveAttribute('tabindex', '-1');
  });

  it('dopo Annulla il focus passa a «Invia file» (il pulsante Annulla sparisce)', async () => {
    await mount();
    await chooseAndSend();
    await user.click(screen.getByRole('button', { name: 'Annulla' }));
    expect(screen.getByRole('button', { name: 'Invia file' })).toHaveFocus();
  });

  it('un errore di validazione alla scelta non sposta il focus', async () => {
    await mount();
    input().focus();
    await choose(new File(['x'], 'note.txt'));
    await screen.findByRole('alert');
    expect(screen.getByRole('alert')).not.toHaveFocus();
  });
});

describe('pagina Carica: validazione prima dell\'invio', () => {
  it('un .txt è un errore del client e non parte nessuna richiesta', async () => {
    await mount();
    await choose(new File(['ciao'], 'note.txt'));
    expect(alertText()).toBe('Tipo di file non supportato: sono ammessi solo file .gpx e .fit.');
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('l\'estensione si controlla senza distinguere le maiuscole', async () => {
    await mount();
    await choose(new File(['<gpx/>'], 'CORSA.GPX'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeEnabled();
  });

  it('un file vuoto (0 byte) è un errore del client e non parte nessuna richiesta', async () => {
    await mount();
    await choose(new File([], 'vuoto.gpx'));
    expect(alertText()).toBe('Il file è vuoto.');
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('esattamente 32 MiB è ammesso e inviato', async () => {
    await mount();
    await chooseAndSend(fileOfSize('grande.gpx', 32 * MIB));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(FakeXhr.instances).toHaveLength(1);
  });

  it('32 MiB + 1 byte è un errore del client e non parte nessuna richiesta', async () => {
    await mount();
    await choose(fileOfSize('enorme.gpx', 32 * MIB + 1));
    expect(alertText()).toBe(`Il file è troppo grande: il limite è 32${NBSP}MiB.`);
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('il pulsante Carica non invia un file rifiutato nemmeno se premuto (disattivato)', async () => {
    await mount();
    await choose(new File(['ciao'], 'note.txt'));
    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    expect(FakeXhr.instances).toHaveLength(0);
  });
});

describe('pagina Carica: errori del server', () => {
  const problems = [
    ['invalid-file-field', 400, 'La richiesta non contiene un solo file valido. Scegli un file e riprova.'],
    ['malformed-request', 400, 'Il servizio non ha riconosciuto la richiesta di caricamento. Riprova.'],
    ['empty-file', 400, 'Il file è vuoto.'],
    ['unsupported-file-type', 415, 'Tipo di file non supportato: sono ammessi solo file .gpx e .fit.'],
    ['file-too-large', 413, `Il file è troppo grande: il limite è 32${NBSP}MiB.`],
    ['invalid-activity-file', 422, "Il file non contiene un'attività GPX o FIT valida."],
    ['too-many-points', 422, 'Il file contiene troppi punti per essere importato.'],
    ['parse-timeout', 422, "L'analisi del file ha richiesto troppo tempo."],
    ['event-too-large', 422, "L'attività è troppo grande per essere importata."],
    ['internal-error', 500, 'Errore interno del servizio durante il caricamento. Riprova più tardi.'],
  ] as const;

  it.each(problems)('%s (%i) mostra il messaggio tradotto, mai il detail del server', async (code, status, message) => {
    await mount();
    await chooseAndSend();
    act(() =>
      FakeXhr.last().respond(status, {
        type: `urn:metrica:problem:${code}`,
        title: 'TITOLO-GREZZO',
        status,
        detail: 'DETTAGLIO-GREZZO del server in italiano',
      }),
    );
    await screen.findByRole('alert');
    expect(alertText()).toBe(message);
    expect(document.body.textContent).not.toContain('GREZZO');
    expect(pendingUploads.getSnapshot()).toEqual([]);
    expect(screen.queryByRole('link', { name: 'Vai alle attività' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it('il 413 del gateway (ProblemDetails senza type di Metrica) usa il messaggio di file troppo grande', async () => {
    await mount();
    await chooseAndSend();
    act(() =>
      FakeXhr.last().respond(413, {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.14',
        title: 'Richiesta troppo grande.',
        status: 413,
        detail: 'DETTAGLIO-GREZZO',
      }),
    );
    await screen.findByRole('alert');
    expect(alertText()).toBe(`Il file è troppo grande: il limite è 32${NBSP}MiB.`);
    expect(document.body.textContent).not.toContain('GREZZO');
  });

  it.each([
    [502, '', 'Servizio non raggiungibile. Riprova più tardi.'],
    [504, '', 'Tempo scaduto: il servizio non ha risposto. Riprova.'],
    [400, '', 'Richiesta non valida.'],
    [404, '', 'Il servizio ha rifiutato il caricamento.'],
    [503, '<html>Service Unavailable</html>', 'Il servizio ha avuto un problema. Riprova più tardi.'],
    [500, '', 'Il servizio ha avuto un problema. Riprova più tardi.'],
  ])('%i con corpo vuoto o non JSON mostra il messaggio per stato, senza crash', async (status, body, message) => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(status, body));
    await screen.findByRole('alert');
    expect(alertText()).toBe(message);
  });

  it('un type sconosciuto dà il messaggio generico della classe di stato (4xx e 5xx)', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(422, { type: 'urn:metrica:problem:nuovo', detail: 'DETTAGLIO-GREZZO' }));
    await screen.findByRole('alert');
    expect(alertText()).toBe('Il servizio ha rifiutato il caricamento.');
    expect(document.body.textContent).not.toContain('GREZZO');

    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    act(() => FakeXhr.last().respond(500, { type: 'urn:metrica:problem:nuovo' }));
    await screen.findByText('Il servizio ha avuto un problema. Riprova più tardi.');
  });

  it('un 200 con JSON inatteso mostra "risposta inattesa" senza crash né registrazione', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(200, { qualcosa: 'altro' }));
    await screen.findByRole('alert');
    expect(alertText()).toBe('Risposta inattesa dal servizio.');
    expect(pendingUploads.getSnapshot()).toEqual([]);
  });

  it('dopo un errore il file resta scelto e si può riprovare', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(502, ''));
    await screen.findByRole('alert');
    expect(screen.getByText('corsa.gpx')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    expect(await screen.findByText('File caricato: è in elaborazione.')).toBeInTheDocument();
  });

  it('il nome del file non compare mai nei messaggi di errore', async () => {
    await mount();
    await chooseAndSend(gpx('nome-riservato.gpx'));
    act(() => FakeXhr.last().respond(422, { type: 'urn:metrica:problem:invalid-activity-file' }));
    await screen.findByRole('alert');
    expect(alertText()).not.toContain('nome-riservato');
  });
});

describe('pagina Carica: errori di rete', () => {
  it('con un file piccolo dice "connessione interrotta"', async () => {
    await mount();
    await chooseAndSend(gpx());
    act(() => FakeXhr.last().failNetwork());
    await screen.findByRole('alert');
    expect(alertText()).toBe('Connessione interrotta. Controlla la rete e riprova.');
  });

  it('con un file oltre 8 MiB dà un testo neutro con il suggerimento (senza dare la colpa alla dimensione)', async () => {
    await mount();
    await chooseAndSend(fileOfSize('grande.gpx', 8 * MIB + 1));
    act(() => FakeXhr.last().failNetwork());
    await screen.findByRole('alert');
    expect(alertText()).toBe(
      `Connessione interrotta durante l'invio. Con file grandi riprova con una rete più stabile; il limite è 32${NBSP}MiB.`,
    );
    expect(alertText()).not.toMatch(/troppo grande/i);
  });

  it('con esattamente 8 MiB resta il messaggio semplice', async () => {
    await mount();
    await chooseAndSend(fileOfSize('otto.gpx', 8 * MIB));
    act(() => FakeXhr.last().failNetwork());
    await screen.findByRole('alert');
    expect(alertText()).toBe('Connessione interrotta. Controlla la rete e riprova.');
  });

  it('il messaggio di rete non contiene testi di eccezione né il nome del file', async () => {
    await mount();
    await chooseAndSend(gpx('percorso-riservato.gpx'));
    act(() => FakeXhr.last().failNetwork());
    await screen.findByRole('alert');
    expect(alertText()).not.toMatch(/riservato|TypeError|Failed to fetch|XMLHttpRequest/i);
  });
});

describe('pagina Carica: ripartire con un altro file', () => {
  it('dopo un esito positivo si può caricare un altro file', async () => {
    await mount();
    await chooseAndSend(gpx('prima.gpx'));
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');

    await choose(gpx('seconda.gpx'));
    expect(screen.queryByText('File caricato: è in elaborazione.')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Vai alle attività' })).not.toBeInTheDocument();
    expect(screen.getByText('seconda.gpx')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    expect(FakeXhr.instances).toHaveLength(2);
    expect((FakeXhr.last().sentFile() as File).name).toBe('seconda.gpx');
    act(() =>
      FakeXhr.last().respond(202, { ...ACCEPTED, uploadId: '55555555-5555-5555-5555-555555555555', externalId: 'e'.repeat(64) }),
    );
    await screen.findByText('File caricato: è in elaborazione.');
    expect(pendingUploads.getSnapshot().map((p) => p.externalId)).toEqual([EXTERNAL_ID, 'e'.repeat(64)]);
  });

  it('dopo un errore scegliere un altro file toglie il messaggio di errore', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(422, { type: 'urn:metrica:problem:invalid-activity-file' }));
    await screen.findByRole('alert');

    await choose(gpx('altro.gpx'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('altro.gpx')).toBeInTheDocument();
  });

  it('si può scegliere di nuovo lo stesso file (l\'input viene azzerato)', async () => {
    await mount();
    const same = gpx();
    await chooseAndSend(same);
    act(() => FakeXhr.last().respond(502, ''));
    await screen.findByRole('alert');
    expect(input().value).toBe('');
    await choose(same);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeEnabled();
  });

  it('scegliere un file non valido dopo uno valido sostituisce la scelta e segnala l\'errore', async () => {
    await mount();
    await choose(gpx('buono.gpx'));
    await choose(new File(['x'], 'cattivo.txt'));
    expect(alertText()).toContain('Tipo di file non supportato');
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
  });

  it('annullare la finestra di scelta (nessun file) non cambia lo stato', async () => {
    await mount();
    await choose(gpx());
    fireEvent.change(input(), { target: { files: [] } });
    expect(screen.getByText('corsa.gpx')).toBeInTheDocument();
  });
});

describe('pagina Carica: trascinamento (da 768 px)', () => {
  const zone = () => screen.getByRole('group', { name: 'Area di selezione del file' });

  it('il drop di un file valido lo sceglie', async () => {
    await mount();
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [gpx('trascinato.gpx')] } });
    expect(screen.getByText('trascinato.gpx')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeEnabled();
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('il drop di un file non valido mostra lo stesso errore della scelta normale', async () => {
    await mount();
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [new File(['x'], 'foto.png')] } });
    expect(alertText()).toBe('Tipo di file non supportato: sono ammessi solo file .gpx e .fit.');
    expect(screen.getByRole('button', { name: 'Invia file' })).toBeDisabled();
  });

  it('il drop di un file oltre 32 MiB è rifiutato senza richieste', async () => {
    await mount();
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [fileOfSize('enorme.fit', 32 * MIB + 1)] } });
    expect(alertText()).toBe(`Il file è troppo grande: il limite è 32${NBSP}MiB.`);
    expect(FakeXhr.instances).toHaveLength(0);
  });

  it('il drop impedisce l\'azione predefinita del browser (aprire il file)', async () => {
    await mount();
    const event = new Event('drop', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: { types: ['Files'], files: [gpx()] } });
    act(() => {
      zone().dispatchEvent(event);
    });
    expect(event.defaultPrevented).toBe(true);
  });

  it('il dragover è accettato (preventDefault) e evidenzia l\'area', async () => {
    await mount();
    const event = new Event('dragover', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: { files: [], types: ['Files'] } });
    act(() => {
      zone().dispatchEvent(event);
    });
    expect(event.defaultPrevented).toBe(true);
    expect(zone()).toHaveAttribute('data-dragging', 'true');
    fireEvent.dragLeave(zone());
    expect(zone()).toHaveAttribute('data-dragging', 'false');
  });

  it('durante l\'invio i file trascinati sono ignorati', async () => {
    await mount();
    await chooseAndSend(gpx('primo.gpx'));
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [gpx('secondo.gpx')] } });
    expect(screen.getByText('primo.gpx')).toBeInTheDocument();
    expect(screen.queryByText('secondo.gpx')).not.toBeInTheDocument();
  });

  it('un drop senza file non cambia nulla', async () => {
    await mount();
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [] } });
    expect(screen.getByText('Nessun file scelto')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('con più file trascinati si prende il primo', async () => {
    await mount();
    fireEvent.drop(zone(), { dataTransfer: { types: ['Files'], files: [gpx('uno.gpx'), gpx('due.gpx')] } });
    expect(screen.getByText('uno.gpx')).toBeInTheDocument();
    expect(screen.queryByText('due.gpx')).not.toBeInTheDocument();
  });

  it('l\'evidenziazione scatta solo se il trascinamento contiene file (non per un testo)', async () => {
    await mount();
    const textDrag = new Event('dragover', { bubbles: true, cancelable: true });
    Object.defineProperty(textDrag, 'dataTransfer', { value: { files: [], types: ['text/plain'] } });
    act(() => {
      zone().dispatchEvent(textDrag);
    });
    expect(zone()).toHaveAttribute('data-dragging', 'false');
    expect(textDrag.defaultPrevented).toBe(false);

    const fileDrag = new Event('dragover', { bubbles: true, cancelable: true });
    Object.defineProperty(fileDrag, 'dataTransfer', { value: { files: [], types: ['Files'] } });
    act(() => {
      zone().dispatchEvent(fileDrag);
    });
    expect(zone()).toHaveAttribute('data-dragging', 'true');
  });

  it('un drop senza file (es. testo) sull\'area non sceglie nulla e non viene bloccato', async () => {
    await mount();
    const event = new Event('drop', { bubbles: true, cancelable: true });
    Object.defineProperty(event, 'dataTransfer', { value: { files: [], types: ['text/plain'] } });
    act(() => {
      zone().dispatchEvent(event);
    });
    expect(event.defaultPrevented).toBe(false);
    expect(screen.getByText('Nessun file scelto')).toBeInTheDocument();
  });

  function windowDrag(type: 'dragover' | 'drop', types: string[], target: EventTarget = document.body) {
    const event = new Event(type, { bubbles: true, cancelable: true });
    const dataTransfer = { files: [], types, dropEffect: 'copy' };
    Object.defineProperty(event, 'dataTransfer', { value: dataTransfer });
    act(() => {
      target.dispatchEvent(event);
    });
    return { event, dataTransfer };
  }

  it('un file lasciato FUORI dall\'area non fa navigare il browser (dragover e drop bloccati a livello di window)', async () => {
    await mount();
    const over = windowDrag('dragover', ['Files']);
    expect(over.event.defaultPrevented).toBe(true);
    expect(over.dataTransfer.dropEffect).toBe('none');
    expect(windowDrag('drop', ['Files']).event.defaultPrevented).toBe(true);
    // Un file lasciato fuori non viene scelto.
    expect(screen.getByText('Nessun file scelto')).toBeInTheDocument();
  });

  it('il blocco a livello di window vale solo per i file (un testo trascinato non è toccato)', async () => {
    await mount();
    expect(windowDrag('dragover', ['text/plain']).event.defaultPrevented).toBe(false);
    expect(windowDrag('drop', ['text/plain']).event.defaultPrevented).toBe(false);
  });

  it('sull\'area il cursore resta «copy» (il gestore di window non lo sovrascrive)', async () => {
    await mount();
    const over = windowDrag('dragover', ['Files'], zone());
    expect(over.event.defaultPrevented).toBe(true);
    expect(over.dataTransfer.dropEffect).toBe('copy');
  });

  it('allo smontaggio il gestore di window viene rimosso', async () => {
    const { unmount } = await mount();
    unmount();
    expect(windowDrag('dragover', ['Files']).event.defaultPrevented).toBe(false);
    expect(windowDrag('drop', ['Files']).event.defaultPrevented).toBe(false);
  });

  it('il suggerimento di trascinamento compare solo da 768 px (hidden md:block)', async () => {
    await mount();
    expect(screen.getByText(/trascina/i)).toHaveClass('hidden', 'md:block');
  });
});

describe('pagina Carica: layout robusto (nessuno scorrimento orizzontale)', () => {
  it('il nome del file lungo si tronca o va a capo, dentro un contenitore con min-w-0', async () => {
    await mount();
    const longName = `${'a'.repeat(200)}.gpx`;
    await choose(gpx(longName));
    const nameElement = screen.getByText(longName);
    expect(nameElement.className).toMatch(/break-all|truncate/);
    expect(nameElement.closest('[class*="min-w-0"]')).not.toBeNull();
  });
});

describe('pagina Carica: lingua e riservatezza', () => {
  it('in inglese mostra gli stessi esiti tradotti', async () => {
    await mount();
    await act(async () => {
      await i18n.changeLanguage('en');
    });
    expect(screen.getByRole('heading', { level: 1, name: 'Upload a file' })).toBeInTheDocument();
    await user.upload(screen.getByLabelText('Choose a file'), gpx());
    await user.click(screen.getByRole('button', { name: 'Send file' }));
    act(() => FakeXhr.last().respond(200, { ...ACCEPTED, status: 'duplicate' }));
    expect(await screen.findByText('This file had already been uploaded.')).toBeInTheDocument();
  });

  it('non scrive nulla sulla console durante un caricamento, riuscito o no', async () => {
    const spies = (['log', 'info', 'warn', 'error', 'debug'] as const).map((m) =>
      vi.spyOn(console, m).mockImplementation(() => undefined),
    );
    await mount();
    await chooseAndSend(gpx('riservato.gpx'));
    act(() => FakeXhr.last().emitProgress(10, 100));
    act(() => FakeXhr.last().failNetwork());
    await screen.findByRole('alert');
    await user.click(screen.getByRole('button', { name: 'Invia file' }));
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    await screen.findByText('File caricato: è in elaborazione.');
    for (const spy of spies) expect(spy).not.toHaveBeenCalled();
  });

  it('l\'esito positivo è dentro la regione di stato e l\'errore dentro un alert', async () => {
    await mount();
    await chooseAndSend();
    act(() => FakeXhr.last().respond(202, ACCEPTED));
    const status = screen.getByRole('status');
    expect(await within(status).findByText('File caricato: è in elaborazione.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
