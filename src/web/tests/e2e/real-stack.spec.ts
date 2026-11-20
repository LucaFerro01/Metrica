import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { italianLight, reachActivityLink } from './support';

// Prova sullo STACK VERO: Vite (5173) → gateway (5080) → ingestion (5081) → NATS → activities (5082) → PostgreSQL.
// Nessuna intercettazione di rete. Si salta salvo `E2E_REAL=1` e richiede lo stack già avviato (vedi README di `web`):
//   $env:E2E_REAL = '1'; pnpm -C src/web test:e2e real-stack
// Gira solo nel progetto desktop (una sola volta): ogni esecuzione carica file diversi (UUID nel creator),
// quindi non confligge con i dati già presenti nel database di sviluppo, ma ne aggiunge TRE a ogni esecuzione
// (il GPX del test «accepted/duplicate», quello dell'AC-001 e quello con data del 2020).

const SAMPLE = fileURLToPath(
  new URL('../../../services/ingestion/tests/TriMetric.Ingestion.Tests/Samples/run.gpx', import.meta.url),
);
const SAMPLE_CREATOR = 'creator="TriMetric test sample"';

test.skip(process.env.E2E_REAL !== '1', 'prova sullo stack reale: impostare E2E_REAL=1 con lo stack avviato');
test.use({ storageState: italianLight });

// Una sola esecuzione: nel progetto desktop (1280 px), non in quello a 375 px.
test.skip(({ viewport }) => (viewport?.width ?? 0) < 768, 'una sola esecuzione: nel progetto desktop');

async function activitiesTotal(request: APIRequestContext): Promise<number> {
  const response = await request.get('/api/activities?pageSize=1');
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { total: number };
  return body.total;
}

/**
 * Il GPX di esempio con un `creator` unico: contenuto (e quindi SHA-256) diverso a ogni esecuzione.
 * `year`: se indicato, le date della traccia (nel campione 2024-05-01) si spostano a quell'anno (es. 2020).
 */
function uniqueGpx(year?: number): Buffer {
  let text = readFileSync(SAMPLE, 'utf8');
  expect(text).toContain(SAMPLE_CREATOR);
  text = text.replace(SAMPLE_CREATOR, `creator="TriMetric e2e ${randomUUID()}"`);
  if (year !== undefined) {
    expect(text).toContain('2024-05-01');
    text = text.replaceAll('2024-05-01', `${year}-03-01`);
  }
  return Buffer.from(text, 'utf8');
}

interface ListedActivity {
  id: string;
  externalId: string;
  sport: string;
  startTime: string;
  distanceMeters: number;
  elapsedSeconds: number;
}

/** Carica un file dalla pagina Carica e restituisce l'esito vero del server (202 con l'externalId). */
async function uploadFromUi(page: Page, name: string, buffer: Buffer): Promise<string> {
  await page.goto('/upload');
  await page.getByLabel('Scegli un file').setInputFiles({ name, mimeType: 'application/gpx+xml', buffer });
  const uploadResponse = page.waitForResponse(
    (response) => response.url().endsWith('/api/ingestion/uploads') && response.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Invia file' }).click();
  const response = await uploadResponse;
  expect(response.status()).toBe(202);
  const body = (await response.json()) as { externalId: string };
  expect(body.externalId).toMatch(/^[0-9a-f]{64}$/);
  await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.', { timeout: 15_000 });
  return body.externalId;
}

/** L'attività importata con questo externalId, cercata con il filtro esatto (DEC-017); `null` se non c'è ancora. */
async function findByExternalId(request: APIRequestContext, externalId: string): Promise<ListedActivity | null> {
  const response = await request.get(`/api/activities?externalId=${externalId}&pageSize=1`);
  expect(response.status()).toBe(200);
  const body = (await response.json()) as { items: ListedActivity[] };
  return body.items[0] ?? null;
}

test('GPX valido: accepted, l\'attività arriva in activities; di nuovo: duplicate', async ({ page, request }) => {
  const before = await activitiesTotal(request);
  const buffer = uniqueGpx();
  const file = { name: 'corsa-reale.gpx', mimeType: 'application/gpx+xml', buffer };

  await page.goto('/upload');
  await page.getByLabel('Scegli un file').setInputFiles(file);
  // L'externalId lo restituisce il server (SHA-256 minuscolo del contenuto): lo si legge dalla risposta vera.
  const uploadResponse = page.waitForResponse(
    (response) => response.url().endsWith('/api/ingestion/uploads') && response.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Invia file' }).click();
  const response = await uploadResponse;
  expect(response.status()).toBe(202);
  const body = (await response.json()) as { externalId: string; uploadId: string; status: string };
  expect(body.status).toBe('accepted');
  expect(body.externalId).toMatch(/^[0-9a-f]{64}$/);
  await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.', { timeout: 15_000 });
  await expect(page.getByRole('alert')).toHaveCount(0);

  // Catena fino a activities (NATS): il totale cresce. Nessuna attesa fissa: polling con scadenza.
  await expect.poll(() => activitiesTotal(request), { timeout: 30_000 }).toBeGreaterThan(before);

  // Stesso file di nuovo: duplicato, nessuna nuova attività.
  await page.getByLabel('Scegli un file').setInputFiles(file);
  await page.getByRole('button', { name: 'Invia file' }).click();
  await expect(page.getByRole('status')).toContainText('Questo file era già stato caricato.', { timeout: 15_000 });
  // `>= before + 1`: il duplicato non aggiunge nulla, ma un altro caricamento concorrente (anche di un altro
  // utente di sviluppo sullo stesso database) potrebbe far crescere il totale; l'unicità del file è già provata
  // dal messaggio «duplicate». Il controllo esatto (`=== before + 1`) sarebbe fragile.
  expect(await activitiesTotal(request)).toBeGreaterThanOrEqual(before + 1);
});

test('GPX non ben formato: 422 con messaggio tradotto', async ({ page }) => {
  await page.goto('/upload');
  await page.getByLabel('Scegli un file').setInputFiles({
    name: 'rotto.gpx',
    mimeType: 'application/gpx+xml',
    buffer: Buffer.from('<gpx version="1.1"><trk><trkseg><trkpt lat="45.0" lon="7.5"', 'utf8'),
  });
  await page.getByRole('button', { name: 'Invia file' }).click();
  await expect(page.getByRole('alert')).toHaveText("Il file non contiene un'attività GPX o FIT valida.", {
    timeout: 15_000,
  });
  await expect(page.getByRole('status')).not.toContainText('caricato');
});

test("AC-001: dopo l'upload l'attività compare in elenco entro pochi secondi, con i suoi dati, e il dettaglio si apre", async ({ page, request }) => {
  const externalId = await uploadFromUi(page, 'corsa-ac001.gpx', uniqueGpx());
  await page.getByRole('link', { name: 'Vai alle attività' }).click();
  await expect(page).toHaveURL(/\/activities$/);
  const table = page.getByRole('table', { name: 'Elenco delle attività' });
  await expect(table).toBeVisible({ timeout: 15_000 });

  // La riga «in elaborazione» (se l'importazione non è già finita) lascia il posto all'attività vera.
  await expect.poll(async () => (await findByExternalId(request, externalId)) !== null, { timeout: 15_000 }).toBe(true);
  const activity = await findByExternalId(request, externalId);
  expect(activity).not.toBeNull();
  await expect(page.getByText('In elaborazione…')).toHaveCount(0, { timeout: 20_000 }); // l'upload esce dallo store
  const link = await reachActivityLink(page, activity?.id ?? 'mancante');

  // Data, sport, distanza e durata nella riga (valori reali del campione: corsa del 2024, un minuto).
  const row = table.getByRole('row').filter({ has: page.locator(`a[href^="/activities/${activity?.id}"]`) });
  await expect(row).toContainText('Corsa');
  await expect(row).toContainText('2024');
  await expect(row).toContainText(/\d+(?:[.,]\d+)?\s(?:m|km)/);
  await expect(row).toContainText('1:00');

  // Il dettaglio si apre dal link e mostra il riepilogo.
  await link.click();
  await expect(page.getByRole('heading', { level: 1, name: 'Corsa' })).toBeVisible();
  await expect(page.getByText('Distanza', { exact: true })).toBeVisible();
  await expect(page.getByText('FC media', { exact: true })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)).toBeLessThanOrEqual(0);
});

test('un file con data vecchia (2020) compare comunque: la riga «in elaborazione» si risolve con externalId', async ({ page, request }) => {
  const externalId = await uploadFromUi(page, 'corsa-2020.gpx', uniqueGpx(2020));
  await page.getByRole('link', { name: 'Vai alle attività' }).click();
  await expect(page.getByRole('table', { name: 'Elenco delle attività' })).toBeVisible({ timeout: 15_000 });

  // La riga «in elaborazione» scompare (l'upload esce dallo store) quando il server ha importato il file.
  await expect(page.getByText('In elaborazione…')).toHaveCount(0, { timeout: 20_000 });
  await expect(page.getByText('Ancora in elaborazione.')).toHaveCount(0);

  // L'attività esiste, con la data del 2020 (ordinata per data di inizio, non per momento di caricamento).
  const activity = await findByExternalId(request, externalId);
  expect(activity?.startTime).toMatch(/^2020-03-01/);

  // E si raggiunge dall'elenco (con pochi dati è nella prima pagina, altrimenti si scorrono le pagine).
  const link = await reachActivityLink(page, activity?.id ?? 'mancante');
  await link.click();
  await expect(page.getByRole('heading', { level: 1, name: 'Corsa' })).toBeVisible();
});
