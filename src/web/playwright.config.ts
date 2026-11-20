import { defineConfig, devices } from '@playwright/test';

// Test end-to-end. NON vengono eseguiti da `pnpm test` (Vitest li esclude).
// Prerequisito umano, una tantum: scaricare il browser (centinaia di MB):
//   pnpm -C src/web exec playwright install chromium
// Poi: `pnpm -C src/web test:e2e`. Le chiamate a /api/* sono intercettate con `page.route` (smoke e upload),
// quindi non servono né gateway né servizi. Unica eccezione: `real-stack.spec.ts` non intercetta nulla, si
// salta salvo `E2E_REAL=1` e richiede lo stack avviato (infrastruttura, ingestion, activities, gateway).

const BASE_URL = 'http://localhost:5173';

export default defineConfig({
  testDir: 'tests/e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list']],
  use: {
    baseURL: BASE_URL,
    trace: 'retain-on-failure',
  },
  // Avvia (o riusa) il dev server di Vite sulla porta 5173. Senza `--host`: non si espone sulla rete (DEC-015).
  webServer: {
    command: 'pnpm dev',
    url: BASE_URL,
    reuseExistingServer: true,
    timeout: 60_000,
  },
  projects: [
    {
      name: 'telefono-375',
      use: { ...devices['Desktop Chrome'], viewport: { width: 375, height: 812 } },
    },
    {
      name: 'desktop-1280',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
  ],
});
