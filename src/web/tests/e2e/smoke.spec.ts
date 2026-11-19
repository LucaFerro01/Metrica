import { expect, test } from '@playwright/test';

// Test di fumo (T16): redirect, navigazione per modalità di layout e assenza di scorrimento orizzontale.
// Scritto in T16, non eseguito finché non sono installati i browser di Playwright.

const PHONE_WIDTH_LIMIT = 768;

// Lingua e tema di partenza deterministici (italiano, chiaro) tramite lo stato del browser: a differenza
// di uno script di avvio, non viene riapplicato a ogni ricaricamento (il test finale ne ha bisogno).
test.use({
  storageState: {
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
  },
});

test.beforeEach(async ({ page }) => {
  // Nessun backend: ogni chiamata a /api/* è simulata.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ items: [], total: 0 }) }),
  );
});

test('"/" porta a /activities', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/\/activities$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Attività' })).toBeVisible();
});

test('la navigazione cambia con la larghezza: in basso sotto 768 px, laterale da 768 px', async ({ page }) => {
  await page.goto('/activities');
  const width = page.viewportSize()?.width ?? 0;
  const bottom = page.getByRole('navigation', { name: 'Navigazione in basso' });
  const side = page.getByRole('navigation', { name: 'Barra laterale' });

  if (width < PHONE_WIDTH_LIMIT) {
    await expect(bottom).toBeVisible();
    await expect(side).toBeHidden();
    await bottom.getByRole('link', { name: 'Carica' }).click();
  } else {
    await expect(side).toBeVisible();
    await expect(bottom).toBeHidden();
    await side.getByRole('link', { name: 'Carica' }).click();
  }
  await expect(page).toHaveURL(/\/upload$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Carica un file' })).toBeVisible();
});

for (const path of ['/activities', '/upload', '/activities/abc-123']) {
  test(`nessuno scorrimento orizzontale su ${path}`, async ({ page }) => {
    await page.goto(path);
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - window.innerWidth,
    );
    expect(overflow).toBeLessThanOrEqual(0);
  });
}

test('il cambio di lingua e di tema funziona e resta dopo il ricaricamento', async ({ page }) => {
  await page.goto('/activities');
  await page.getByRole('group', { name: 'Lingua' }).getByRole('button', { name: 'English' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Activities' })).toBeVisible();
  await page.getByRole('group', { name: 'Theme' }).getByRole('button', { name: 'Dark' }).click();
  await expect(page.locator('html')).toHaveClass(/dark/);

  await page.reload();
  await expect(page.locator('html')).toHaveClass(/dark/);
  await expect(page.getByRole('heading', { level: 1, name: 'Activities' })).toBeVisible();
});
