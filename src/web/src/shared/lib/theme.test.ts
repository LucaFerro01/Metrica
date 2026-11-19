import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installFakeMatchMedia } from '@/test/matchMedia';
import { resetPersistedPreferences } from '@/test/resetPreferences';
import { storage } from './storage';
import {
  getThemePreference,
  initTheme,
  resolveTheme,
  setThemePreference,
  subscribeThemePreference,
  THEME_STORAGE_KEY,
} from './theme';

const root = document.documentElement;
let dispose: (() => void) | undefined;

function startTheme() {
  dispose = initTheme();
}

beforeEach(() => {
  resetPersistedPreferences();
  root.classList.remove('dark');
});

afterEach(() => {
  dispose?.();
  dispose = undefined;
});

describe('resolveTheme', () => {
  it('chiaro e scuro ignorano il sistema', () => {
    expect(resolveTheme('light', true)).toBe('light');
    expect(resolveTheme('dark', false)).toBe('dark');
  });

  it('sistema segue la preferenza del sistema', () => {
    expect(resolveTheme('system', true)).toBe('dark');
    expect(resolveTheme('system', false)).toBe('light');
  });
});

describe('initTheme', () => {
  it('senza scelta salvata la preferenza è "sistema" e segue il sistema scuro', () => {
    installFakeMatchMedia(true);
    startTheme();
    expect(getThemePreference()).toBe('system');
    expect(root).toHaveClass('dark');
  });

  it('senza scelta salvata e sistema chiaro non applica la classe', () => {
    installFakeMatchMedia(false);
    startTheme();
    expect(root).not.toHaveClass('dark');
  });

  it('una scelta salvata "dark" vince sul sistema chiaro', () => {
    installFakeMatchMedia(false);
    storage.set(THEME_STORAGE_KEY, 'dark');
    startTheme();
    expect(getThemePreference()).toBe('dark');
    expect(root).toHaveClass('dark');
  });

  it('una scelta salvata "light" vince sul sistema scuro', () => {
    installFakeMatchMedia(true);
    storage.set(THEME_STORAGE_KEY, 'light');
    startTheme();
    expect(getThemePreference()).toBe('light');
    expect(root).not.toHaveClass('dark');
  });

  it('un valore salvato non valido è trattato come "sistema"', () => {
    installFakeMatchMedia(true);
    storage.set(THEME_STORAGE_KEY, 'viola');
    startTheme();
    expect(getThemePreference()).toBe('system');
    expect(root).toHaveClass('dark');
  });

  it('funziona anche senza matchMedia (tema chiaro)', () => {
    vi.stubGlobal('matchMedia', undefined);
    startTheme();
    expect(root).not.toHaveClass('dark');
  });
});

describe('setThemePreference', () => {
  it('applica la classe e salva la scelta con storage', () => {
    installFakeMatchMedia(false);
    startTheme();

    setThemePreference('dark');
    expect(root).toHaveClass('dark');
    expect(storage.get(THEME_STORAGE_KEY)).toBe('dark');
    expect(getThemePreference()).toBe('dark');

    setThemePreference('light');
    expect(root).not.toHaveClass('dark');
    expect(storage.get(THEME_STORAGE_KEY)).toBe('light');

    setThemePreference('system');
    expect(storage.get(THEME_STORAGE_KEY)).toBe('system');
  });

  it('la scelta persiste: un nuovo avvio la rilegge', () => {
    installFakeMatchMedia(false);
    startTheme();
    setThemePreference('dark');
    dispose?.();
    root.classList.remove('dark');

    startTheme();
    expect(getThemePreference()).toBe('dark');
    expect(root).toHaveClass('dark');
  });

  it('notifica gli iscritti solo quando la preferenza cambia', () => {
    installFakeMatchMedia(false);
    startTheme();
    let calls = 0;
    const unsubscribe = subscribeThemePreference(() => {
      calls += 1;
    });
    setThemePreference('dark');
    setThemePreference('dark');
    setThemePreference('light');
    unsubscribe();
    setThemePreference('system');
    expect(calls).toBe(2);
  });
});

describe('modalità sistema', () => {
  it('segue il cambio di prefers-color-scheme a pagina aperta', () => {
    const media = installFakeMatchMedia(false);
    startTheme();
    expect(root).not.toHaveClass('dark');

    media.setMatches(true);
    expect(root).toHaveClass('dark');

    media.setMatches(false);
    expect(root).not.toHaveClass('dark');
  });

  it('con una scelta esplicita ignora il cambio del sistema', () => {
    const media = installFakeMatchMedia(false);
    startTheme();
    setThemePreference('light');

    media.setMatches(true);
    expect(root).not.toHaveClass('dark');

    setThemePreference('dark');
    media.setMatches(false);
    expect(root).toHaveClass('dark');
  });

  it('tornando a "sistema" riallinea subito la classe al sistema', () => {
    const media = installFakeMatchMedia(true);
    startTheme();
    setThemePreference('light');
    expect(root).not.toHaveClass('dark');

    setThemePreference('system');
    expect(root).toHaveClass('dark');
    media.setMatches(false);
    expect(root).not.toHaveClass('dark');
  });

  it('la funzione di chiusura rimuove il listener del sistema', () => {
    const media = installFakeMatchMedia(false);
    startTheme();
    expect(media.listenerCount()).toBe(1);
    dispose?.();
    dispose = undefined;
    expect(media.listenerCount()).toBe(0);
  });
});

describe('nessun lampo di tema sbagliato (script inline di index.html)', () => {
  const html = readFileSync(path.resolve(import.meta.dirname, '../../../index.html'), 'utf8');
  const doc = new DOMParser().parseFromString(html, 'text/html');
  const inline = [...doc.head.querySelectorAll('script:not([src])')].map((s) => s.textContent ?? '');

  function runInlineScript() {
    for (const code of inline) {
      new Function(code)();
    }
  }

  it("lo script inline sta nell'head, prima del modulo dell'app", () => {
    expect(inline.length).toBeGreaterThan(0);
    const scripts = [...doc.querySelectorAll('script')];
    const firstInline = scripts.findIndex((s) => !s.getAttribute('src'));
    const moduleIndex = scripts.findIndex((s) => s.getAttribute('type') === 'module');
    expect(firstInline).toBeGreaterThanOrEqual(0);
    expect(moduleIndex).toBeGreaterThan(firstInline);
  });

  it('legge la stessa chiave di storage usata da theme.ts', () => {
    const keys = inline.flatMap((code) =>
      [...code.matchAll(/getItem\(\s*['"]([^'"]+)['"]\s*\)/g)].map((m) => m[1]),
    );
    expect(keys).toEqual([THEME_STORAGE_KEY]);
  });

  it('applica la classe prima del primo disegno secondo la scelta salvata', () => {
    installFakeMatchMedia(false);
    storage.set(THEME_STORAGE_KEY, 'dark');
    runInlineScript();
    expect(root).toHaveClass('dark');
  });

  it('una scelta "light" salvata vince sul sistema scuro', () => {
    installFakeMatchMedia(true);
    storage.set(THEME_STORAGE_KEY, 'light');
    root.classList.add('dark');
    runInlineScript();
    expect(root).not.toHaveClass('dark');
  });

  it('senza scelta salvata segue il sistema', () => {
    installFakeMatchMedia(true);
    runInlineScript();
    expect(root).toHaveClass('dark');
  });

  it('con localStorage che lancia non lancia e segue il sistema', () => {
    installFakeMatchMedia(true);
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('negato', 'SecurityError');
    });
    expect(runInlineScript).not.toThrow();
    expect(root).toHaveClass('dark');
  });

  it('senza matchMedia e senza scelta non lancia e lascia il tema chiaro', () => {
    vi.stubGlobal('matchMedia', undefined);
    expect(runInlineScript).not.toThrow();
    expect(root).not.toHaveClass('dark');
  });
});
