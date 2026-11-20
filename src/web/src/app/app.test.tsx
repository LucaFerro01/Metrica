import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { i18n } from '@/shared/i18n';
import { storage } from '@/shared/lib/storage';
import { THEME_STORAGE_KEY } from '@/shared/lib/theme';
import { activityId, jsonResponse, listBody, makeDetail, stubActivitiesApi } from '@/test/fakeActivitiesApi';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { renderApp } from '@/test/renderApp';

let disposeTheme: (() => void) | undefined;

async function mount(path = '/') {
  const result = await renderApp(path);
  disposeTheme = result.disposeTheme;
  return result;
}

beforeEach(() => {
  // Le pagine Attività leggono l'API: qui un elenco vuoto (il loro comportamento è provato nei test delle feature).
  stubActivitiesApi(() => jsonResponse(listBody([])));
  resetPersistedPreferences();
  document.documentElement.classList.remove('dark');
  installFakeMatchMedia(false);
});

afterEach(async () => {
  disposeTheme?.();
  disposeTheme = undefined;
  await i18n.changeLanguage('it');
});

describe('instradamento', () => {
  it('"/" porta a /activities', async () => {
    const { router } = await mount('/');
    expect(router.state.location.pathname).toBe('/activities');
    expect(await screen.findByRole('heading', { level: 1, name: 'Attività' })).toBeInTheDocument();
  });

  it('/upload mostra la pagina di caricamento', async () => {
    await mount('/upload');
    expect(await screen.findByRole('heading', { level: 1, name: 'Carica un file' })).toBeInTheDocument();
  });

  it("/activities/$activityId mostra il dettaglio dell'attività", async () => {
    stubActivitiesApi(() => jsonResponse(makeDetail(5, { sport: 'swim' })));
    await mount(`/activities/${activityId(5)}`);
    expect(await screen.findByRole('heading', { level: 1, name: 'Nuoto' })).toBeInTheDocument();
  });

  it('/activities/$activityId con un id che non è un UUID mostra «Attività non trovata»', async () => {
    await mount('/activities/abc-123');
    expect(await screen.findByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "Torna all'elenco" })).toHaveAttribute('href', '/activities');
  });

  it('i parametri di ricerca non validi di /activities si ignorano (mai una pagina di errore)', async () => {
    const { router } = await mount('/activities?sport=triathlon&page=-1');
    expect(await screen.findByRole('heading', { level: 1, name: 'Attività' })).toBeInTheDocument();
    expect(router.state.matches.at(-1)?.search).toEqual({});
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Pagina non trovata' })).not.toBeInTheDocument();
  });

  it('un percorso sconosciuto mostra "pagina non trovata" con il link per tornare alle attività', async () => {
    await mount('/non-esiste');
    expect(await screen.findByRole('heading', { name: 'Pagina non trovata' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Vai alle attività' })).toHaveAttribute('href', '/activities');
  });
});

describe('layout a due modalità (le media query non si valutano in jsdom: si verificano le classi)', () => {
  it('ha una navigazione in basso solo sotto 768 px (md:hidden)', async () => {
    await mount('/activities');
    const bottom = await screen.findByRole('navigation', { name: 'Navigazione in basso' });
    expect(bottom).toHaveClass('md:hidden');
    expect(bottom).not.toHaveClass('hidden');
  });

  it('ha una barra laterale solo da 768 px (hidden md:flex)', async () => {
    await mount('/activities');
    const side = await screen.findByRole('navigation', { name: 'Barra laterale' });
    expect(side).toHaveClass('hidden', 'md:flex');
  });

  it.each(['Navigazione in basso', 'Barra laterale'])('la navigazione "%s" ha i link ad Attività e Carica', async (name) => {
    await mount('/activities');
    const nav = await screen.findByRole('navigation', { name });
    expect(within(nav).getByRole('link', { name: 'Attività' })).toHaveAttribute('href', '/activities');
    expect(within(nav).getByRole('link', { name: 'Carica' })).toHaveAttribute('href', '/upload');
  });

  it('segna la voce corrente con aria-current', async () => {
    await mount('/upload');
    const nav = await screen.findByRole('navigation', { name: 'Navigazione in basso' });
    expect(within(nav).getByRole('link', { name: 'Carica' })).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Attività' })).not.toHaveAttribute('aria-current');
  });

  it('naviga con un clic sul link', async () => {
    const user = userEvent.setup();
    const { router } = await mount('/activities');
    const nav = await screen.findByRole('navigation', { name: 'Navigazione in basso' });
    await user.click(within(nav).getByRole('link', { name: 'Carica' }));
    expect(router.state.location.pathname).toBe('/upload');
    expect(await screen.findByRole('heading', { level: 1, name: 'Carica un file' })).toBeInTheDocument();
  });

  it('rispetta i margini sicuri dello schermo e usa dvh', async () => {
    const { container } = await mount('/activities');
    const bottom = await screen.findByRole('navigation', { name: 'Navigazione in basso' });
    expect(bottom.className).toContain('pb-[env(safe-area-inset-bottom)]');
    const header = screen.getByRole('banner');
    expect(header.className).toContain('pt-[env(safe-area-inset-top)]');
    expect(container.innerHTML).toContain('min-h-dvh');
  });

  it('i controlli della navigazione hanno bersagli di tocco di almeno 44 px (min-h-11 = 2,75 rem)', async () => {
    await mount('/activities');
    const bottom = await screen.findByRole('navigation', { name: 'Navigazione in basso' });
    for (const link of within(bottom).getAllByRole('link')) {
      expect(link).toHaveClass('min-h-11');
    }
  });

  it('il contenuto può restringersi (min-w-0) per non allargare la pagina', async () => {
    await mount('/activities');
    expect(await screen.findByRole('main')).toHaveClass('min-w-0');
  });
});

describe('interruttore del tema', () => {
  it('ha tre posizioni con etichette tradotte e "Sistema" attiva di partenza', async () => {
    await mount('/activities');
    const group = await screen.findByRole('group', { name: 'Tema' });
    const buttons = within(group).getAllByRole('button');
    expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual(['Sistema', 'Chiaro', 'Scuro']);
    expect(within(group).getByRole('button', { name: 'Sistema' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'Chiaro' })).toHaveAttribute('aria-pressed', 'false');
    expect(within(group).getByRole('button', { name: 'Scuro' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('"Scuro" applica la classe dark, salva la scelta e aggiorna aria-pressed', async () => {
    const user = userEvent.setup();
    await mount('/activities');
    const group = await screen.findByRole('group', { name: 'Tema' });
    await user.click(within(group).getByRole('button', { name: 'Scuro' }));
    expect(document.documentElement).toHaveClass('dark');
    expect(storage.get(THEME_STORAGE_KEY)).toBe('dark');
    expect(within(group).getByRole('button', { name: 'Scuro' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'Sistema' })).toHaveAttribute('aria-pressed', 'false');

    await user.click(within(group).getByRole('button', { name: 'Chiaro' }));
    expect(document.documentElement).not.toHaveClass('dark');
    expect(storage.get(THEME_STORAGE_KEY)).toBe('light');
  });

  it('i pulsanti hanno bersagli di tocco di almeno 44 px', async () => {
    await mount('/activities');
    const group = await screen.findByRole('group', { name: 'Tema' });
    for (const button of within(group).getAllByRole('button')) {
      expect(button).toHaveClass('min-h-11', 'min-w-11');
    }
  });
});

describe('cambio lingua', () => {
  it('ha due posizioni, con l\'italiano attivo di partenza', async () => {
    await mount('/activities');
    const group = await screen.findByRole('group', { name: 'Lingua' });
    expect(within(group).getByRole('button', { name: 'Italiano' })).toHaveAttribute('aria-pressed', 'true');
    expect(within(group).getByRole('button', { name: 'English' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('passando a English cambiano i testi della navigazione e della pagina, e l\'attributo lang', async () => {
    const user = userEvent.setup();
    await mount('/activities');
    const group = await screen.findByRole('group', { name: 'Lingua' });
    await user.click(within(group).getByRole('button', { name: 'English' }));

    const nav = await screen.findByRole('navigation', { name: 'Bottom navigation' });
    expect(within(nav).getByRole('link', { name: 'Activities' })).toBeInTheDocument();
    expect(within(nav).getByRole('link', { name: 'Upload' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Theme' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Activities' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('en');
  });
});
