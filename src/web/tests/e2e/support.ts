import { expect, type Page } from '@playwright/test';

/** Stato del browser di partenza deterministico: italiano e tema chiaro (le chiavi sono quelle di `shared`). */
export const italianLight = {
  cookies: [],
  origins: [
    {
      origin: 'http://localhost:5173',
      localStorage: [
        { name: 'metrica.language', value: 'it' },
        { name: 'metrica.theme', value: 'light' },
      ],
    },
  ],
};

/** Eccedenza orizzontale della pagina in pixel: deve essere <= 0 (nessuno scorrimento orizzontale). */
export function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
}

/**
 * Raggiunge nella tabella dell'elenco la riga dell'attività `id`, scorrendo le pagine se non è nella prima.
 * Dopo ogni clic su «Successiva» ATTENDE davvero la nuova pagina: con `keepPreviousData` l'elenco precedente resta
 * (attenuato) mentre arriva il nuovo, e controllare link e «Successiva» subito salterebbe pagine. Teoricamente
 * «URL = ?page=N e nessun `aria-busy`» può valere un istante con i dati vecchi (l'indirizzo cambia prima del
 * disegno con l'elenco attenuato); per questo si attende anche la risposta dell'elenco per quella pagina (la
 * richiesta parte dopo quel disegno, quindi `aria-busy` è già vero) e solo dopo che `aria-busy` è tornato falso
 * si guardano link e «Successiva». Restituisce il link, visibile.
 */
export async function reachActivityLink(page: Page, id: string) {
  const busy = page.locator('[aria-busy="true"]');
  const table = page.getByRole('table', { name: 'Elenco delle attività' });
  const link = table.locator(`a[href^="/activities/${id}"]`);
  for (let current = 1; current <= 50; current += 1) {
    await expect(table).toBeVisible();
    await expect(busy).toHaveCount(0);
    if ((await link.count()) > 0) {
      await expect(link).toBeVisible();
      return link;
    }
    const next = page.getByRole('button', { name: 'Successiva' });
    if (await next.isDisabled()) {
      // Ultima pagina: l'elenco può non essersi ancora aggiornato (arriva col ciclo di polling da 2 s): si attende.
      await expect(link, `attività ${id} non trovata in ${current} pagine dell'elenco`).toBeVisible({ timeout: 15_000 });
      return link;
    }
    const loaded = page.waitForResponse((response) => {
      const url = new URL(response.url());
      return url.pathname === '/api/activities' && url.searchParams.get('page') === String(current + 1);
    });
    await next.click();
    await loaded;
    await expect(page).toHaveURL(new RegExp(`/activities\\?page=${current + 1}$`));
    await expect(busy).toHaveCount(0);
  }
  throw new Error(`attività ${id} non trovata entro 50 pagine`);
}
