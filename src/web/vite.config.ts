import { fileURLToPath, URL } from 'node:url';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { configDefaults, defineConfig } from 'vitest/config';

// Gateway locale (DEC-009): il dev server inoltra /api, quindi il browser non fa richieste
// cross-origin e non serve CORS. Il proxy gira lato server: non espone nulla sulla rete.
const GATEWAY = 'http://127.0.0.1:5080';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': GATEWAY },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // I test end-to-end (Playwright) hanno un proprio runner: Vitest non deve raccoglierli.
    exclude: [...configDefaults.exclude, 'tests/e2e/**'],
    unstubEnvs: true,
    unstubGlobals: true,
    restoreMocks: true,
  },
});
