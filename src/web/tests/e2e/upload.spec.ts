import { expect, test, type Page, type Route } from '@playwright/test';
import { horizontalOverflow, italianLight } from './support';

// Pagina Carica (T17) con l'API simulata: nessun servizio reale (`page.route`). Girano a 375 px e a 1280 px
// (progetti di `playwright.config.ts`). La prova sullo stack vero è in `real-stack.spec.ts`.

const PHONE_WIDTH_LIMIT = 768;
const UPLOAD_URL = '**/api/ingestion/uploads';
const ACCEPTED = {
  uploadId: '11111111-2222-3333-4444-555555555555',
  externalId: 'f'.repeat(64),
  status: 'accepted',
};

test.use({ storageState: italianLight });

function gpxFile(name = 'corsa.gpx') {
  return { name, mimeType: 'application/gpx+xml', buffer: Buffer.from('<gpx version="1.1"></gpx>') };
}

/** Risponde alla richiesta di upload e conta quante ne arrivano. */
async function mockUpload(
  page: Page,
  respond: (route: Route) => Promise<void> | void,
): Promise<{ count: () => number }> {
  let requests = 0;
  await page.route(UPLOAD_URL, async (route) => {
    requests += 1;
    await respond(route);
  });
  return { count: () => requests };
}

const accepted = (route: Route) =>
  route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify(ACCEPTED) });

async function expectNoHorizontalScroll(page: Page) {
  expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
}

test.describe('layout', () => {
  test('nessuno scorrimento orizzontale: pagina iniziale, nome file lunghissimo, dopo un esito', async ({ page }) => {
    await mockUpload(page, accepted);
    await page.goto('/upload');
    await expect(page.getByRole('heading', { level: 1, name: 'Carica un file' })).toBeVisible();
    await expectNoHorizontalScroll(page);

    // Nome senza spazi né punti di interruzione: il caso peggiore per il layout.
    const longName = `${'x'.repeat(300)}.gpx`;
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile(longName));
    const nameElement = page.getByText(longName);
    await expect(nameElement).toBeVisible();
    await expectNoHorizontalScroll(page);

    // Il nome sta dentro la viewport (non solo "la pagina non scorre").
    const box = await nameElement.boundingBox();
    const viewportWidth = page.viewportSize()?.width ?? 0;
    expect(box).not.toBeNull();
    expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(viewportWidth);

    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.');
    await expectNoHorizontalScroll(page);
  });

  test('nessuno scorrimento orizzontale dopo un errore', async ({ page }) => {
    await mockUpload(page, (route) =>
      route.fulfill({
        status: 422,
        contentType: 'application/problem+json',
        body: JSON.stringify({ type: 'urn:trimetric:problem:too-many-points', status: 422 }),
      }),
    );
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile(`${'y'.repeat(300)}.gpx`));
    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('alert')).toContainText('troppi punti');
    await expectNoHorizontalScroll(page);
  });

  test('i controlli principali hanno un bersaglio di tocco di almeno 44 px', async ({ page }) => {
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile());
    for (const target of [page.getByText('Scegli un file', { exact: true }), page.getByRole('button', { name: 'Invia file' })]) {
      const box = await target.boundingBox();
      expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
      expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
    }
  });

  test('il suggerimento di trascinamento compare solo da 768 px', async ({ page }) => {
    await page.goto('/upload');
    const hint = page.getByText('Oppure trascina qui il file.');
    const width = page.viewportSize()?.width ?? 0;
    if (width < PHONE_WIDTH_LIMIT) await expect(hint).toBeHidden();
    else await expect(hint).toBeVisible();
  });
});

test.describe('flussi', () => {
  test('flusso felice: scelta del file, invio, 202 simulato, esito e link all\'elenco', async ({ page }) => {
    const api = await mockUpload(page, accepted);
    await page.goto('/upload');
    await expect(page.getByRole('button', { name: 'Invia file' })).toBeDisabled();

    await page.getByLabel('Scegli un file').setInputFiles(gpxFile('corsa.gpx'));
    await expect(page.getByText('corsa.gpx')).toBeVisible();
    await page.getByRole('button', { name: 'Invia file' }).click();

    await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.');
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(api.count()).toBe(1);

    await page.getByRole('link', { name: 'Vai alle attività' }).click();
    await expect(page).toHaveURL(/\/activities$/);
  });

  test('flusso di errore: 422 simulato con messaggio tradotto, mai il testo del server', async ({ page }) => {
    await mockUpload(page, (route) =>
      route.fulfill({
        status: 422,
        contentType: 'application/problem+json',
        body: JSON.stringify({
          type: 'urn:trimetric:problem:invalid-activity-file',
          title: 'TITOLO-DEL-SERVER',
          status: 422,
          detail: 'DETTAGLIO-DEL-SERVER',
        }),
      }),
    );
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile());
    await page.getByRole('button', { name: 'Invia file' }).click();

    const alert = page.getByRole('alert');
    await expect(alert).toHaveText("Il file non contiene un'attività GPX o FIT valida.");
    await expect(page.locator('body')).not.toContainText('DEL-SERVER');
  });

  test('un file .txt è rifiutato dal client: nessuna richiesta parte', async ({ page }) => {
    const api = await mockUpload(page, accepted);
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles({ name: 'note.txt', mimeType: 'text/plain', buffer: Buffer.from('ciao') });
    await expect(page.getByRole('alert')).toContainText('Tipo di file non supportato');
    await expect(page.getByRole('button', { name: 'Invia file' })).toBeDisabled();
    expect(api.count()).toBe(0);
  });

  test('una risposta 502 a corpo vuoto (gateway) mostra "servizio non raggiungibile"', async ({ page }) => {
    await mockUpload(page, (route) => route.fulfill({ status: 502, body: '' }));
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile());
    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('alert')).toHaveText('Servizio non raggiungibile. Riprova più tardi.');
  });

  test('un errore di rete mostra "connessione interrotta"', async ({ page }) => {
    await mockUpload(page, (route) => route.abort('connectionreset'));
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile());
    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('alert')).toHaveText('Connessione interrotta. Controlla la rete e riprova.');
  });

  test('durante l\'invio c\'è la barra di avanzamento e Annulla interrompe senza esito', async ({ page }) => {
    // La richiesta resta in sospeso finché il test non la rilascia: si vede lo stato "in invio".
    let release: () => void = () => undefined;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    await mockUpload(page, async (route) => {
      await held;
      await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify(ACCEPTED) }).catch(() => undefined);
    });
    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles(gpxFile());
    await page.getByRole('button', { name: 'Invia file' }).click();

    await expect(page.getByRole('progressbar', { name: 'Avanzamento del caricamento' })).toBeVisible();
    await expect(page.getByRole('progressbar')).toHaveAttribute('aria-valuenow', /^\d+$/);
    await expect(page.getByRole('button', { name: 'Invia file' })).toBeDisabled();

    await page.getByRole('button', { name: 'Annulla' }).click();
    await expect(page.getByRole('progressbar')).toHaveCount(0);
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect(page.getByRole('status')).not.toContainText('caricato');
    release();
  });

  test('da 768 px il trascinamento sceglie il file con la stessa validazione', async ({ page }) => {
    test.skip((page.viewportSize()?.width ?? 0) < PHONE_WIDTH_LIMIT, 'il trascinamento è previsto da 768 px');
    await mockUpload(page, accepted);
    await page.goto('/upload');
    const zone = page.getByRole('group', { name: 'Area di selezione del file' });

    const dropFile = async (name: string) => {
      const dataTransfer = await page.evaluateHandle((fileName) => {
        const transfer = new DataTransfer();
        transfer.items.add(new File(['<gpx/>'], fileName));
        return transfer;
      }, name);
      await zone.dispatchEvent('drop', { dataTransfer });
    };

    await dropFile('foto.png');
    await expect(page.getByRole('alert')).toContainText('Tipo di file non supportato');

    await dropFile('trascinato.gpx');
    await expect(page.getByText('trascinato.gpx')).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.');
  });
});
