// Tema chiaro/scuro (DEC-016, REQ-039). La preferenza ha tre valori: sistema (segue
// `prefers-color-scheme`, anche quando cambia a pagina aperta), chiaro, scuro. La scelta si conserva
// con `shared/lib/storage` e si applica come classe `dark` su <html>; i colori veri sono variabili CSS
// in `src/index.css`. Lo script inline di `index.html` applica la stessa classe PRIMA del primo
// disegno (nessun lampo di tema sbagliato) leggendo la stessa chiave: un test ne verifica la coincidenza.

import { storage } from './storage';

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_PREFERENCES: readonly ThemePreference[] = ['system', 'light', 'dark'];

/** Chiave di storage della preferenza. Deve coincidere con quella letta dallo script inline di `index.html`. */
export const THEME_STORAGE_KEY = 'metrica.theme';

const DARK_QUERY = '(prefers-color-scheme: dark)';

function isThemePreference(value: string | null): value is ThemePreference {
  return value === 'system' || value === 'light' || value === 'dark';
}

function readStoredPreference(): ThemePreference {
  const stored = storage.get(THEME_STORAGE_KEY);
  return isThemePreference(stored) ? stored : 'system';
}

function systemQuery(): MediaQueryList | null {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(DARK_QUERY)
    : null;
}

export function resolveTheme(preference: ThemePreference, systemPrefersDark: boolean): ResolvedTheme {
  if (preference === 'system') return systemPrefersDark ? 'dark' : 'light';
  return preference;
}

function applyResolvedTheme(theme: ResolvedTheme): void {
  document.documentElement.classList.toggle('dark', theme === 'dark');
}

function applyPreference(preference: ThemePreference): void {
  applyResolvedTheme(resolveTheme(preference, systemQuery()?.matches ?? false));
}

// Piccolo store esterno: `useSyncExternalStore` lo legge in `useTheme`.
let current: ThemePreference | null = null;
const listeners = new Set<() => void>();

export function getThemePreference(): ThemePreference {
  current ??= readStoredPreference();
  return current;
}

export function subscribeThemePreference(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Cambia la preferenza: la salva, aggiorna la classe su <html> e avvisa gli iscritti. */
export function setThemePreference(preference: ThemePreference): void {
  if (getThemePreference() !== preference) {
    current = preference;
    for (const listener of [...listeners]) listener();
  }
  storage.set(THEME_STORAGE_KEY, preference);
  applyPreference(preference);
}

/**
 * Legge la preferenza salvata, applica il tema e, finché la preferenza è "sistema", segue i cambi di
 * `prefers-color-scheme`. Restituisce la funzione che toglie il listener (da chiamare allo smontaggio).
 */
export function initTheme(): () => void {
  current = readStoredPreference();
  applyPreference(current);

  const query = systemQuery();
  if (!query) return () => undefined;

  const onChange = (event: MediaQueryListEvent) => {
    if (getThemePreference() === 'system') applyResolvedTheme(event.matches ? 'dark' : 'light');
  };
  query.addEventListener('change', onChange);
  return () => {
    query.removeEventListener('change', onChange);
  };
}
