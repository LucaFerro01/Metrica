import js from '@eslint/js';
import { defineConfig, globalIgnores } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

// ---------------------------------------------------------------------------------------------
// Regole di architettura (ARCHITECTURE/04-frontend.md, DEC-015), imposte con `no-restricted-imports`
// e override per cartella, senza plugin aggiuntivi:
//   1. una feature non importa da un'altra feature (né con l'alias `@/features/...` né con percorsi
//      relativi) e non importa da `app`;
//   2. `shared` non importa da `features` né da `app`;
//   3. da fuori, una feature si importa solo dal suo `index.ts` (`@/features/<nome>`), mai dai file interni.
//
// Come funziona senza conoscere i nomi delle feature: dentro una feature (e dentro `shared`) vale
// "i percorsi relativi non escono dalla propria cartella radice". Un file a profondità k sotto la
// radice esce con k+1 salti `../`, quindi per ogni profondità c'è un override con il proprio modello
// (`../**` a profondità 0, `../../**` a profondità 1, ...). Dentro la propria feature si usano
// percorsi relativi; per importare da `shared` si usa l'alias `@/shared/...`.
//
// LIMITE NOTO: gli override coprono fino a MAX_DEPTH livelli di cartelle sotto la radice della
// feature o di `shared` (qui 7, oltre il necessario). Un file più profondo non è coperto dalla regola
// sui percorsi relativi (resta coperta quella sugli alias).
// ---------------------------------------------------------------------------------------------

const MAX_DEPTH = 7;
const SRC_FILES = '{ts,tsx}';

/** Modelli che vietano un import relativo che esce dalla cartella radice, per un file a profondità `depth`. */
function climbOut(depth, message) {
  return { group: [`${'../'.repeat(depth + 1)}**`], message };
}

function depthFiles(root, depth) {
  return `${root}/${'*/'.repeat(depth)}*.${SRC_FILES}`;
}

const featureAliasPatterns = [
  {
    group: ['@/features', '@/features/**'],
    message:
      'Una feature non importa da altre feature (né dalla propria con l\'alias): condividi tramite `shared/` o usa percorsi relativi dentro la feature.',
  },
  {
    group: ['@/app', '@/app/**'],
    message: 'Una feature non importa da `app`: la dipendenza va da `app` verso le feature, non il contrario.',
  },
];

const sharedAliasPatterns = [
  {
    group: ['@/features', '@/features/**'],
    message: '`shared` non può importare da `features`: dipende dal basso verso l\'alto.',
  },
  {
    group: ['@/app', '@/app/**'],
    message: '`shared` non può importare da `app`.',
  },
];

const featureRelativeMessage =
  'Percorso relativo che esce dalla feature: per importare da `shared` usa `@/shared/...`; da altre feature non si importa.';
const sharedRelativeMessage =
  'Percorso relativo che esce da `shared`: `shared` non può importare da `features` né da `app`.';

const boundaryConfigs = [
  // 3. Da fuori delle feature e di shared (app, main, test, ...): solo l'interfaccia pubblica di una feature.
  {
    files: [`src/**/*.${SRC_FILES}`],
    ignores: ['src/features/**', 'src/shared/**'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: ['@/features/*/**', '**/features/*/**'],
              message:
                'Importa una feature solo dal suo index (`@/features/<nome>`), mai dai file interni.',
            },
          ],
        },
      ],
    },
  },
  // 1. Dentro le feature, per ogni profondità.
  ...Array.from({ length: MAX_DEPTH + 1 }, (_, depth) => ({
    files: [depthFiles('src/features/*', depth)],
    rules: {
      'no-restricted-imports': [
        'error',
        { patterns: [...featureAliasPatterns, climbOut(depth, featureRelativeMessage)] },
      ],
    },
  })),
  // 2. Dentro shared, per ogni profondità.
  ...Array.from({ length: MAX_DEPTH + 1 }, (_, depth) => ({
    files: [depthFiles('src/shared', depth)],
    rules: {
      'no-restricted-imports': [
        'error',
        { patterns: [...sharedAliasPatterns, climbOut(depth, sharedRelativeMessage)] },
      ],
    },
  })),
];

// ---------------------------------------------------------------------------------------------
// Accesso al browser (DEC-015, REQ-038): `localStorage` solo da `shared/lib/storage.ts`; mai
// `sessionStorage`, mai `window.open` (il guscio nativo non ha popup). Copre l'uso diretto, via
// `window.`/`globalThis.`/`self.` e la destrutturazione da `window`.
// ---------------------------------------------------------------------------------------------

const STORAGE_MESSAGE =
  'Usa `storage` da `@/shared/lib/storage`: è l\'unico accesso consentito a localStorage (guscio nativo, modalità privata).';
const OPEN_MESSAGE =
  'window.open non è disponibile nel guscio nativo (nessun popup): usa un link o la navigazione dell\'app.';
const BROWSER_OBJECTS = ['window', 'globalThis', 'self'];
const RESTRICTED_PROPERTIES = [
  ['localStorage', STORAGE_MESSAGE],
  ['sessionStorage', STORAGE_MESSAGE],
  ['open', OPEN_MESSAGE],
];

const browserApiRules = {
  'no-restricted-globals': [
    'error',
    { name: 'localStorage', message: STORAGE_MESSAGE },
    { name: 'sessionStorage', message: STORAGE_MESSAGE },
    { name: 'open', message: OPEN_MESSAGE },
  ],
  'no-restricted-properties': [
    'error',
    ...BROWSER_OBJECTS.flatMap((object) =>
      RESTRICTED_PROPERTIES.map(([property, message]) => ({ object, property, message })),
    ),
  ],
  'no-restricted-syntax': [
    'error',
    {
      selector:
        "VariableDeclarator[init.type='Identifier'][init.name=/^(window|globalThis|self)$/] > ObjectPattern > Property[key.name=/^(localStorage|sessionStorage|open)$/]",
      message: STORAGE_MESSAGE,
    },
  ],
};

export default defineConfig([
  globalIgnores([
    'dist',
    'node_modules',
    'coverage',
    'playwright-report',
    'test-results',
    // Tipi generati da `pnpm gen:api`: non si modificano a mano e non si controllano.
    'src/shared/api/*.d.ts',
  ]),

  {
    files: ['**/*.{ts,tsx,js}'],
    extends: [js.configs.recommended, tseslint.configs.recommended],
  },

  {
    files: ['src/**/*.{ts,tsx}'],
    extends: [reactHooks.configs.flat.recommended, reactRefresh.configs.vite],
    languageOptions: { globals: globals.browser },
  },

  {
    files: ['*.{ts,js}', 'tests/**/*.ts'],
    languageOptions: { globals: globals.node },
  },

  ...boundaryConfigs,

  {
    files: [`src/**/*.${SRC_FILES}`, `tests/**/*.${SRC_FILES}`],
    rules: browserApiRules,
  },

  // Unico modulo autorizzato a toccare localStorage (e il suo test).
  {
    files: ['src/shared/lib/storage.ts', 'src/shared/lib/storage.test.ts'],
    rules: {
      'no-restricted-globals': 'off',
      'no-restricted-properties': 'off',
      'no-restricted-syntax': 'off',
    },
  },
]);
