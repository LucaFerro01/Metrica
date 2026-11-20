import type { Page } from '@playwright/test';

/** Stato del browser di partenza deterministico: italiano e tema chiaro (le chiavi sono quelle di `shared`). */
export const italianLight = {
  cookies: [],
  origins: [
    {
      origin: 'http://localhost:5173',
      localStorage: [
        { name: 'trimetric.language', value: 'it' },
        { name: 'trimetric.theme', value: 'light' },
      ],
    },
  ],
};

/** Eccedenza orizzontale della pagina in pixel: deve essere <= 0 (nessuno scorrimento orizzontale). */
export function horizontalOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
}
