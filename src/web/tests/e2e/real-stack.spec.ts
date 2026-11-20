import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { italianLight } from './support';

// Prova sullo STACK VERO: Vite (5173) → gateway (5080) → ingestion (5081) → NATS → activities (5082) → PostgreSQL.
// Nessuna intercettazione di rete. Si salta salvo `E2E_REAL=1` e richiede lo stack già avviato (vedi README di `web`):
//   $env:E2E_REAL = '1'; pnpm -C src/web test:e2e real-stack
// Gira solo nel progetto desktop (una sola volta): ogni esecuzione carica un file diverso (UUID nel creator),
// quindi non confligge con i dati già presenti nel database di sviluppo, ma ne aggiunge uno a ogni esecuzione.

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

/** Il GPX di esempio con un `creator` unico: contenuto (e quindi SHA-256) diverso a ogni esecuzione. */
function uniqueGpx(): Buffer {
  const original = readFileSync(SAMPLE, 'utf8');
  expect(original).toContain(SAMPLE_CREATOR);
  return Buffer.from(original.replace(SAMPLE_CREATOR, `creator="TriMetric e2e ${randomUUID()}"`), 'utf8');
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
