import { expect, test, type Page, type Route } from '@playwright/test';
import { horizontalOverflow, italianLight, reachActivityLink } from './support';

// Elenco e dettaglio delle attività (T18) con l'API simulata: nessun servizio reale. Si intercettano solo i
// percorsi che iniziano con /api/ (mai il glob `**/api/**`, che prenderebbe anche i moduli di Vite). Girano a
// 375 px e a 1280 px (progetti di `playwright.config.ts`).

const PHONE_WIDTH_LIMIT = 768;
const LIST_LABEL = 'Elenco delle attività';

test.use({ storageState: italianLight });

const isPhone = (page: Page) => (page.viewportSize()?.width ?? 0) < PHONE_WIDTH_LIMIT;

const uuid = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const externalIdOf = (n: number) => n.toString(16).padStart(64, 'b');

interface Item {
  id: string;
  externalId: string;
  sport: string;
  startTime: string;
  distanceMeters: number;
  elapsedSeconds: number;
  elevationGainMeters: number | null;
}

function item(n: number, overrides: Partial<Item> = {}): Item {
  return {
    id: uuid(n),
    externalId: externalIdOf(n),
    sport: 'run',
    startTime: `2026-11-${String(1 + (n % 28)).padStart(2, '0')}T07:30:00+00:00`,
    distanceMeters: 5000 + n * 137,
    elapsedSeconds: 1800 + n * 11,
    elevationGainMeters: n % 3 === 0 ? null : 50 + n,
    ...overrides,
  };
}

const listBody = (items: Item[], page = 1, total = items.length) => ({ items, page, pageSize: 20, total });

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

function detail(n: number, overrides: Record<string, unknown> = {}) {
  return {
    ...item(n),
    movingSeconds: 1700,
    summary: { avgHeartRate: 151, maxHeartRate: 180, avgPowerWatts: 212, avgCadence: 84 },
    laps: [],
    stream: {
      timeOffsetSeconds: [0, 1800],
      latitude: null,
      longitude: null,
      altitudeMeters: null,
      heartRate: null,
      powerWatts: null,
      cadence: null,
      speedMetersPerSecond: null,
    },
    ...overrides,
  };
}

/** Intercetta le chiamate `/api/*` con un gestore che riceve l'URL; il gestore risponde o lascia 404. */
async function mockApi(page: Page, handler: (url: URL, route: Route) => Promise<void> | void) {
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    async (route) => {
      const url = new URL(route.request().url());
      await handler(url, route);
    },
  );
}

async function expectNoHorizontalScroll(page: Page) {
  expect(await horizontalOverflow(page)).toBeLessThanOrEqual(0);
}

/**
 * La tabella sta per intero nel suo contenitore, senza barra di scorrimento interna: il contenitore ha
 * `overflow-x: auto`, quindi un taglio dei dati non si vedrebbe dall'overflow della pagina.
 */
async function expectTableFitsItsContainer(page: Page) {
  const wrapper = page.getByRole('table', { name: LIST_LABEL }).locator('xpath=..');
  const { scrollWidth, clientWidth } = await wrapper.evaluate((element) => ({
    scrollWidth: element.scrollWidth,
    clientWidth: element.clientWidth,
  }));
  expect(scrollWidth).toBeLessThanOrEqual(clientWidth);
}

/** Dati realistici: venti attività con valori normali e, in coda, una maratona con il dislivello a quattro cifre. */
const realisticItems = [
  ...Array.from({ length: 18 }, (_, i) => item(i + 1, { sport: ['run', 'ride', 'swim', 'other'][i % 4] as string })),
  item(19, { sport: 'ride', distanceMeters: 142_195, elapsedSeconds: 19_800, elevationGainMeters: 1234 }),
  item(20, { sport: 'run', distanceMeters: 42_195, elapsedSeconds: 11_100, elevationGainMeters: 1234 }),
];

/** Valori lunghi al limite: distanza enorme, durate di migliaia di ore, dislivello a otto cifre. */
const extremeItems = Array.from({ length: 20 }, (_, i) =>
  item(i + 1, {
    sport: ['run', 'ride', 'swim', 'other'][i % 4] as string,
    distanceMeters: 987_654_321.9,
    elapsedSeconds: 99_999_999,
    elevationGainMeters: i % 2 ? 99_999_999 : null,
  }),
);

test.describe('elenco: layout', () => {
  test('schede e barra in basso sotto 768 px; tabella e barra laterale da 768 px', async ({ page }) => {
    await mockApi(page, (url, route) =>
      url.pathname === '/api/activities' ? json(route, listBody(Array.from({ length: 20 }, (_, i) => item(i + 1)), 1, 45)) : route.fulfill({ status: 404 }),
    );
    await page.goto('/activities');
    await expect(page.getByRole('heading', { level: 1, name: 'Attività' })).toBeVisible();

    const cards = page.getByRole('list', { name: LIST_LABEL });
    const table = page.getByRole('table', { name: LIST_LABEL });
    const bottom = page.getByRole('navigation', { name: 'Navigazione in basso' });
    const side = page.getByRole('navigation', { name: 'Barra laterale' });
    if (isPhone(page)) {
      await expect(cards).toBeVisible();
      await expect(table).toBeHidden();
      await expect(bottom).toBeVisible();
      await expect(side).toBeHidden();
      await expect(cards.getByRole('listitem')).toHaveCount(20);
    } else {
      await expect(table).toBeVisible();
      await expect(cards).toBeHidden();
      await expect(side).toBeVisible();
      await expect(bottom).toBeHidden();
      await expect(table.getByRole('row')).toHaveCount(21);
    }
    await expectNoHorizontalScroll(page);
    if (!isPhone(page)) await expectTableFitsItsContainer(page);
  });

  test('la tabella con dati realistici sta nel suo contenitore a 768 e a 1280 px (nessun dato tagliato)', async ({ page }) => {
    await mockApi(page, (url, route) =>
      url.pathname === '/api/activities' ? json(route, listBody(realisticItems, 1, 45)) : route.fulfill({ status: 404 }),
    );
    for (const width of [768, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto('/activities');
      await expect(page.getByRole('table', { name: LIST_LABEL })).toBeVisible();
      await expectNoHorizontalScroll(page);
      await expectTableFitsItsContainer(page);
    }
  });

  test('nessuno scorrimento orizzontale con molte righe e valori molto lunghi', async ({ page }) => {
    await mockApi(page, (url, route) =>
      url.pathname === '/api/activities' ? json(route, listBody(extremeItems, 1, 45)) : route.fulfill({ status: 404 }),
    );
    await page.goto('/activities');
    await expect(page.getByRole('heading', { level: 1, name: 'Attività' })).toBeVisible();
    await expect(page.getByText('Pagina 1 di 3')).toBeVisible();
    await expectNoHorizontalScroll(page);

    // I valori lunghi stanno dentro la viewport (non solo «la pagina non scorre»).
    const width = page.viewportSize()?.width ?? 0;
    const container = isPhone(page) ? page.getByRole('list', { name: LIST_LABEL }) : page.getByRole('table', { name: LIST_LABEL });
    const box = await container.boundingBox();
    expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(width);
  });

  test('al confine di 768 px la tabella con valori lunghi non allarga la pagina', async ({ page }) => {
    await page.setViewportSize({ width: 768, height: 900 });
    await mockApi(page, (url, route) =>
      url.pathname === '/api/activities' ? json(route, listBody(extremeItems, 1, 45)) : route.fulfill({ status: 404 }),
    );
    await page.goto('/activities');
    await expect(page.getByRole('table', { name: LIST_LABEL })).toBeVisible();
    await expectNoHorizontalScroll(page);
    // Se i dati non ci stanno, il contenitore scorre da sé (`overflow-x: auto`): non taglia in silenzio (`hidden`).
    const wrapper = page.getByRole('table', { name: LIST_LABEL }).locator('xpath=..');
    expect(await wrapper.evaluate((element) => getComputedStyle(element).overflowX)).toBe('auto');
  });

  test('i controlli (filtro, paginazione, righe) hanno bersagli di tocco di almeno 44 px', async ({ page }) => {
    await mockApi(page, (url, route) =>
      url.pathname === '/api/activities' ? json(route, listBody([item(1), item(2)], 2, 45)) : route.fulfill({ status: 404 }),
    );
    await page.goto('/activities?page=2');
    await expect(page.getByText('Pagina 2 di 3')).toBeVisible();
    const targets = [
      ...(await page.getByRole('group', { name: 'Filtra per sport' }).getByRole('button').all()),
      page.getByRole('button', { name: 'Precedente' }),
      page.getByRole('button', { name: 'Successiva' }),
      isPhone(page)
        ? page.getByRole('list', { name: LIST_LABEL }).getByRole('link').first()
        : page.getByRole('table', { name: LIST_LABEL }).getByRole('link').first(),
    ];
    for (const target of targets) {
      const box = await target.boundingBox();
      expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
      expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
    }
  });
});

test.describe('elenco: flussi', () => {
  test('scorrendo le pagine con risposte lente si raggiunge un\'attività in seconda pagina (l\'attesa tra una pagina e l\'altra è reale)', async ({ page }) => {
    test.skip(isPhone(page), 'la tabella si vede da 768 px');
    const target = item(30, { id: uuid(30) });
    await mockApi(page, async (url, route) => {
      if (url.pathname !== '/api/activities') return route.fulfill({ status: 404 });
      const requested = Number(url.searchParams.get('page') ?? '1');
      await new Promise((resolve) => setTimeout(resolve, 400)); // risposta lenta: l'elenco precedente resta (attenuato) nel frattempo
      const items = requested === 2 ? [target] : [item(requested * 100)];
      return json(route, listBody(items, requested, 45));
    });
    await page.goto('/activities');
    await expect(page.getByText('Pagina 1 di 3')).toBeVisible();
    const link = await reachActivityLink(page, target.id);
    await expect(page).toHaveURL(/\/activities\?page=2$/);
    await expect(link).toBeVisible();
  });

  test('filtro per sport e paginazione cambiano le richieste e l\'indirizzo', async ({ page }) => {
    const requests: URL[] = [];
    await mockApi(page, (url, route) => {
      if (url.pathname !== '/api/activities') return route.fulfill({ status: 404 });
      requests.push(url);
      return json(route, listBody([item(1)], Number(url.searchParams.get('page') ?? '1'), 45));
    });
    await page.goto('/activities');
    await expect(page.getByText('Pagina 1 di 3')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Precedente' })).toBeDisabled();

    await page.getByRole('button', { name: 'Successiva' }).click();
    await expect(page.getByText('Pagina 2 di 3')).toBeVisible();
    await expect(page).toHaveURL(/\/activities\?page=2$/);

    await page.getByRole('group', { name: 'Filtra per sport' }).getByRole('button', { name: 'Bici' }).click();
    await expect(page).toHaveURL(/\/activities\?sport=ride$/);
    await expect(page.getByText('Pagina 1 di 3')).toBeVisible();
    expect(requests.at(-1)?.searchParams.get('sport')).toBe('ride');
    expect(requests.at(-1)?.searchParams.get('page')).toBe('1');
    await expectNoHorizontalScroll(page);
  });

  test('senza attività: messaggio e link a Carica', async ({ page }) => {
    await mockApi(page, (url, route) => (url.pathname === '/api/activities' ? json(route, listBody([])) : route.fulfill({ status: 404 })));
    await page.goto('/activities');
    await expect(page.getByText('Nessuna attività: carica un file per iniziare.')).toBeVisible();
    await page.getByRole('link', { name: 'Carica un file' }).click();
    await expect(page).toHaveURL(/\/upload$/);
  });

  test('errore del gateway: messaggio e «Riprova» che funziona', async ({ page }) => {
    let healthy = false;
    await mockApi(page, (url, route) => {
      if (url.pathname !== '/api/activities') return route.fulfill({ status: 404 });
      return healthy ? json(route, listBody([item(1)])) : route.fulfill({ status: 502, body: '' });
    });
    await page.goto('/activities');
    await expect(page.getByRole('alert')).toContainText('Il servizio non risponde');
    await expectNoHorizontalScroll(page);
    healthy = true;
    await page.getByRole('button', { name: 'Riprova' }).click();
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect(page.getByRole(isPhone(page) ? 'list' : 'table', { name: LIST_LABEL })).toBeVisible();
  });

  test('un clic sulla riga apre il dettaglio e «Torna all\'elenco» ripristina filtro e pagina', async ({ page }) => {
    await mockApi(page, (url, route) => {
      if (url.pathname === '/api/activities') return json(route, listBody([item(1, { sport: 'ride' })], 2, 45));
      if (url.pathname === `/api/activities/${uuid(1)}`) return json(route, detail(1, { sport: 'ride' }));
      return route.fulfill({ status: 404 });
    });
    await page.goto('/activities?sport=ride&page=2');
    const container = page.getByRole(isPhone(page) ? 'list' : 'table', { name: LIST_LABEL });
    await container.getByRole('link').first().click();
    await expect(page).toHaveURL(new RegExp(`/activities/${uuid(1)}\\?sport=ride&page=2$`));
    await expect(page.getByRole('heading', { level: 1, name: 'Bici' })).toBeVisible();
    await page.getByRole('link', { name: "Torna all'elenco" }).click();
    await expect(page).toHaveURL(/\/activities\?sport=ride&page=2$/);
  });
});

test.describe('riga «in elaborazione»', () => {
  const EXTERNAL_ID = externalIdOf(777);

  test('dopo un upload compare in cima e si trasforma in attività quando la ricerca per externalId la trova', async ({ page }) => {
    // Orologio controllabile: i cicli da 2 s si fanno scorrere con `runFor`, senza attese reali.
    await page.clock.install();
    // L'importazione simulata comincia dopo che l'elenco è montato e mostra la riga: la decide il test.
    let imported = false;
    const detectionUrls: URL[] = [];
    const old = () => item(777, { externalId: EXTERNAL_ID, startTime: '2020-03-01T08:00:00+00:00' });
    await mockApi(page, (url, route) => {
      if (url.pathname === '/api/ingestion/uploads') {
        return json(route, { uploadId: '11111111-2222-3333-4444-555555555555', externalId: EXTERNAL_ID, status: 'accepted' }, 202);
      }
      if (url.pathname === '/api/activities') {
        if (url.searchParams.has('externalId')) {
          detectionUrls.push(url); // le asserzioni si fanno fuori dal gestore
          return json(route, listBody(imported ? [old()] : []));
        }
        // L'elenco visibile include l'attività importata solo dopo l'importazione.
        return json(route, listBody(imported ? [item(1), item(2), old()] : [item(1), item(2)]));
      }
      return route.fulfill({ status: 404 });
    });

    await page.goto('/upload');
    await page.getByLabel('Scegli un file').setInputFiles({ name: 'corsa.gpx', mimeType: 'application/gpx+xml', buffer: Buffer.from('<gpx version="1.1"></gpx>') });
    await page.getByRole('button', { name: 'Invia file' }).click();
    await expect(page.getByRole('status')).toContainText('File caricato: è in elaborazione.');
    await page.getByRole('link', { name: 'Vai alle attività' }).click();

    const container = page.getByRole(isPhone(page) ? 'list' : 'table', { name: LIST_LABEL });
    const pendingRow = container.getByText('In elaborazione…');
    await expect(pendingRow).toBeVisible();
    await expectNoHorizontalScroll(page);

    // In cima: la prima riga (dopo l'intestazione, in tabella) è quella «in elaborazione».
    const firstRow = isPhone(page) ? container.getByRole('listitem').first() : container.getByRole('row').nth(1);
    await expect(firstRow).toContainText('In elaborazione…');
    // Finché il server non ha importato, un ciclo di polling non cambia nulla.
    await page.clock.runFor(2100);
    await expect(pendingRow).toBeVisible();

    // Il server importa il file ORA (elenco già montato): al ciclo successivo la riga lascia il posto all'attività.
    imported = true;
    await page.clock.runFor(2100);
    await expect(pendingRow).toHaveCount(0);
    await expect(container.getByRole('link')).toHaveCount(3); // anche con data vecchia, fuori dalla pagina
    await expectNoHorizontalScroll(page);

    // Polling fermo: dieci cicli di orologio non producono nessuna richiesta di rilevamento in più.
    const before = detectionUrls.length;
    await page.clock.runFor(20_000);
    await page.evaluate(() => fetch('/api/ping')); // andata e ritorno dal livello di intercettazione: le richieste precedenti sono già arrivate
    await expect.poll(() => detectionUrls.length, { timeout: 1000 }).toBe(before);

    // Ogni richiesta di rilevamento era per l'externalId esatto, con una sola attività richiesta.
    expect(detectionUrls.length).toBeGreaterThanOrEqual(2);
    for (const url of detectionUrls) {
      expect(url.searchParams.get('externalId')).toBe(EXTERNAL_ID);
      expect(url.searchParams.get('pageSize')).toBe('1');
    }
  });
});

test.describe('dettaglio', () => {
  const laps = Array.from({ length: 30 }, (_, i) => ({
    startTime: new Date(Date.parse('2026-11-19T07:30:00+00:00') + i * 600_000).toISOString(),
    elapsedSeconds: i === 3 ? null : 600 + i,
    distanceMeters: i === 5 ? null : 2500 + i,
  }));

  test('riepilogo e giri: nessuno scorrimento orizzontale, anche con valori molto lunghi', async ({ page }) => {
    await mockApi(page, (url, route) => {
      if (url.pathname === `/api/activities/${uuid(5)}`) {
        expect(url.searchParams.get('maxPoints')).toBe('2');
        return json(
          route,
          detail(5, {
            sport: 'ride',
            startTime: '2026-11-19T07:30:00+00:00',
            distanceMeters: 987_654_321.9,
            elapsedSeconds: 99_999_999,
            movingSeconds: 88_888_888,
            elevationGainMeters: 99_999_999,
            summary: { avgHeartRate: 151, maxHeartRate: 180, avgPowerWatts: 999_999, avgCadence: null },
            laps,
          }),
        );
      }
      return route.fulfill({ status: 404 });
    });
    await page.goto(`/activities/${uuid(5)}`);
    await expect(page.getByRole('heading', { level: 1, name: 'Bici' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 2, name: 'Giri' })).toBeVisible();
    await expect(page.getByText('Cadenza media')).toBeVisible();
    await expect(page.getByText('non disponibile').first()).toBeVisible();
    await expectNoHorizontalScroll(page);

    if (isPhone(page)) await expect(page.getByRole('navigation', { name: 'Navigazione in basso' })).toBeVisible();
    else await expect(page.getByRole('navigation', { name: 'Barra laterale' })).toBeVisible();
  });

  test('attività non trovata (404) e id non valido: stessa pagina, link all\'elenco', async ({ page }) => {
    let calls = 0;
    await mockApi(page, (_url, route) => {
      calls += 1;
      return route.fulfill({ status: 404, body: '' });
    });
    await page.goto(`/activities/${uuid(99)}`);
    await expect(page.getByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeVisible();
    await expectNoHorizontalScroll(page);
    expect(calls).toBeGreaterThanOrEqual(1);

    const before = calls;
    await page.goto('/activities/abc-123');
    await expect(page.getByRole('heading', { level: 1, name: 'Attività non trovata' })).toBeVisible();
    expect(calls).toBe(before); // l'id non valido non chiama la rete
    await page.getByRole('link', { name: "Torna all'elenco" }).click();
    await expect(page).toHaveURL(/\/activities$/);
  });
});
