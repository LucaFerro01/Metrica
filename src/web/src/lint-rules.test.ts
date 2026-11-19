// @vitest-environment node
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { ESLint } from 'eslint';
import { beforeAll, describe, expect, it } from 'vitest';

// Prova che le regole di confine e di accesso al browser scattano davvero (e non scattano sul codice lecito).
// ESLint legge la configurazione reale del progetto; i file hanno un percorso virtuale (non esistono su disco).

const WEB_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const eslint = new ESLint({ cwd: WEB_ROOT, overrideConfigFile: path.join(WEB_ROOT, 'eslint.config.js') });

async function lint(filePath: string, code: string) {
  const [result] = await eslint.lintText(code, { filePath: path.join(WEB_ROOT, filePath) });
  if (!result) throw new Error('nessun risultato da ESLint');
  return result.messages.map((m) => ({ rule: m.ruleId, message: m.message, fatal: m.fatal === true }));
}

// Il primo `lintText` carica configurazione e plugin (qualche secondo): lo si fa una volta sola, qui.
beforeAll(async () => {
  await lint('src/app/warmup.ts', 'export const warmup = 1;\n');
}, 60_000);

async function rulesOf(filePath: string, code: string): Promise<string[]> {
  return (await lint(filePath, code)).map((m) => m.rule ?? 'FATAL');
}

describe('confini tra feature, shared e app (no-restricted-imports)', () => {
  const RULE = 'no-restricted-imports';

  it.each([
    ['alias verso un\'altra feature', 'src/features/activities/list/Page.tsx', `import { x } from '@/features/analytics';\nexport const y = x;\n`],
    ['alias verso un file interno di un\'altra feature', 'src/features/activities/list/Page.tsx', `import { x } from '@/features/analytics/dashboard/Chart';\nexport const y = x;\n`],
    ['percorso relativo verso un\'altra feature (profondità 1)', 'src/features/activities/list/Page.tsx', `import { x } from '../../analytics/index';\nexport const y = x;\n`],
    ['percorso relativo verso un\'altra feature (profondità 0)', 'src/features/activities/store.ts', `import { x } from '../analytics';\nexport const y = x;\n`],
    ['percorso relativo verso un\'altra feature (profondità 2)', 'src/features/activities/detail/parts/Lap.tsx', `import { x } from '../../../planning/index';\nexport const y = x;\n`],
    ['percorso relativo che esce dalla feature verso shared', 'src/features/activities/list/Page.tsx', `import { x } from '../../../shared/lib/format';\nexport const y = x;\n`],
    ['feature che importa da app', 'src/features/activities/list/Page.tsx', `import { x } from '@/app/router';\nexport const y = x;\n`],
    ['shared che importa da una feature (alias)', 'src/shared/lib/format.ts', `import { x } from '@/features/activities';\nexport const y = x;\n`],
    ['shared che importa da app (alias)', 'src/shared/ui/Button.tsx', `import { x } from '@/app/router';\nexport const y = x;\n`],
    ['shared che importa da una feature (relativo)', 'src/shared/lib/format.ts', `import { x } from '../../features/activities';\nexport const y = x;\n`],
    ['shared che importa da app (relativo, profondità 0)', 'src/shared/barrel.ts', `import { x } from '../app/router';\nexport const y = x;\n`],
    ['app che importa un file interno di una feature (alias)', 'src/app/router.tsx', `import { Page } from '@/features/activities/list/ActivityListPage';\nexport const y = Page;\n`],
    ['app che importa un file interno di una feature (relativo)', 'src/app/router.tsx', `import { Page } from '../features/activities/list/ActivityListPage';\nexport const y = Page;\n`],
    ['main.tsx che importa un file interno di una feature', 'src/main.tsx', `import { Page } from '@/features/activities/upload/UploadPage';\nexport const y = Page;\n`],
  ])('vieta: %s', async (_name, file, code) => {
    expect(await rulesOf(file, code)).toContain(RULE);
  });

  it.each([
    ['feature: import da shared con alias', 'src/features/activities/list/Page.tsx', `import { x } from '@/shared/lib/format';\nexport const y = x;\n`],
    ['feature: import relativo nella stessa cartella', 'src/features/activities/list/Page.tsx', `import { x } from './Row';\nexport const y = x;\n`],
    ['feature: import relativo tra sottocartelle della stessa feature', 'src/features/activities/list/Page.tsx', `import { x } from '../upload/state';\nexport const y = x;\n`],
    ['feature: import da un pacchetto', 'src/features/activities/list/Page.tsx', `import { useQuery } from '@tanstack/react-query';\nexport const y = useQuery;\n`],
    ['shared: import da un altro modulo di shared (alias)', 'src/shared/lib/format.ts', `import { x } from '@/shared/lib/storage';\nexport const y = x;\n`],
    ['shared: import relativo dentro shared', 'src/shared/api/client.ts', `import { x } from '../lib/storage';\nexport const y = x;\n`],
    ['app: import da una feature tramite il suo index', 'src/app/router.tsx', `import { Page } from '@/features/activities';\nexport const y = Page;\n`],
    ['app: import relativo da una feature tramite il suo index', 'src/app/router.tsx', `import { Page } from '../features/activities';\nexport const y = Page;\n`],
    ['app: import da shared', 'src/app/router.tsx', `import { x } from '@/shared/lib/format';\nexport const y = x;\n`],
  ])('consente: %s', async (_name, file, code) => {
    expect(await lint(file, code)).toEqual([]);
  });
});

describe('accesso diretto al browser (no-restricted-globals, no-restricted-properties, no-restricted-syntax)', () => {
  const FILE = 'src/app/layout/Example.tsx';

  it.each([
    ['localStorage', `export const a = localStorage.getItem('k');\n`],
    ['sessionStorage', `export const a = sessionStorage.getItem('k');\n`],
    ['window.localStorage', `export const a = window.localStorage.getItem('k');\n`],
    ['globalThis.localStorage', `export const a = globalThis.localStorage.getItem('k');\n`],
    ['self.localStorage', `export const a = self.localStorage.getItem('k');\n`],
    ['window.sessionStorage', `export const a = window.sessionStorage.length;\n`],
    ['window[\'localStorage\']', `export const a = window['localStorage'].length;\n`],
    ['destructuring da window', `const { localStorage } = window;\nexport const a = localStorage;\n`],
    ['window.open', `export const a = () => window.open('https://example.test');\n`],
    ['globalThis.open', `export const a = () => globalThis.open('https://example.test');\n`],
    ['open (globale)', `export const a = () => open('https://example.test');\n`],
  ])('vieta %s', async (_name, code) => {
    const messages = await lint(FILE, code);
    expect(messages.length, JSON.stringify(messages)).toBeGreaterThan(0);
    expect(messages.every((m) => !m.fatal)).toBe(true);
  });

  it('vieta localStorage anche dentro una feature e dentro shared (fuori da storage.ts)', async () => {
    const code = `export const a = window.localStorage.getItem('k');\n`;
    expect((await lint('src/features/activities/upload/UploadPage.tsx', code)).length).toBeGreaterThan(0);
    expect((await lint('src/shared/lib/theme.ts', code)).length).toBeGreaterThan(0);
  });

  it('consente localStorage solo in shared/lib/storage.ts', async () => {
    const code = `export const a = window.localStorage.getItem('k');\nexport const b = globalThis.localStorage;\nexport const c = sessionStorage;\n`;
    expect(await lint('src/shared/lib/storage.ts', code)).toEqual([]);
  });

  it('non segnala codice lecito con nomi simili', async () => {
    const code = [
      `export function useThing(dialog: { open: () => void }) {`,
      `  const open = () => dialog.open();`,
      `  const localStorageKey = 'k';`,
      `  return { open, localStorageKey, win: window.location.href };`,
      `}`,
      ``,
    ].join('\n');
    expect(await lint('src/app/layout/Example.tsx', code)).toEqual([]);
  });
});

describe('file generati', () => {
  it('i tipi generati in shared/api sono esclusi dal lint', async () => {
    expect(await eslint.isPathIgnored(path.join(WEB_ROOT, 'src/shared/api/activities.d.ts'))).toBe(true);
    expect(await eslint.isPathIgnored(path.join(WEB_ROOT, 'src/shared/api/ingestion.d.ts'))).toBe(true);
  });

  it('il codice scritto a mano in shared/api non è escluso', async () => {
    expect(await eslint.isPathIgnored(path.join(WEB_ROOT, 'src/shared/api/client.ts'))).toBe(false);
  });
});
